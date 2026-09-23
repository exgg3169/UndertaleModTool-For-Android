using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using UndertaleModLib.Scripting;

namespace UndertaleModTool.Core.Scripting;

/// <summary>
/// Thrown when a script fails to compile.
/// </summary>
public sealed class ScriptCompilationException : Exception
{
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public ScriptCompilationException(ImmutableArray<Diagnostic> diagnostics)
        : base(string.Join("\n", diagnostics.Select(Format)))
    {
        Diagnostics = diagnostics;
    }

    private static string Format(Diagnostic d)
    {
        var span = d.Location.GetMappedLineSpan();
        string where = span.IsValid ? $"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})" : "";
        return $"{where}: {d.Id}: {d.GetMessage()}";
    }
}

/// <summary>
/// Compiles and runs UndertaleModTool C# scripts (.csx).
/// </summary>
/// <remarks>
/// This mirrors what <c>CSharpScript.EvaluateAsync</c> does in the desktop tool, but without
/// Microsoft.CodeAnalysis.Scripting's assumptions that assemblies have a file location on disk
/// (on Android they live inside the APK). Metadata references come from reference assemblies
/// embedded into this app at build time (see the .csproj), and the emitted submission is loaded
/// with <see cref="Assembly.Load(byte[], byte[])"/>.
/// </remarks>
public static class ScriptCompiler
{
    /// <summary>
    /// Namespaces imported by default; same as <c>ScriptingUtil.CreateDefaultScriptOptions</c>.
    /// </summary>
    public static readonly string[] DefaultImports =
    {
        "UndertaleModLib", "UndertaleModLib.Models", "UndertaleModLib.Decompiler",
        "UndertaleModLib.Scripting", "UndertaleModLib.Compiler",
        "System", "System.IO", "System.Collections.Generic",
        "System.Text.RegularExpressions",
    };

    /// <summary>
    /// Assembly whose "refs/*.dll" manifest resources are the reference assemblies for scripts.
    /// The Android app embeds them into itself at build time (see its .csproj). If null, all loaded
    /// assemblies are searched.
    /// </summary>
    public static Assembly ReferenceAssemblySource { get; set; }

    /// <summary>Base directory for relative <c>#load</c> paths of scripts that have no file path.</summary>
    public static string DefaultBaseDirectory { get; set; } = Environment.CurrentDirectory;

    private static ImmutableArray<MetadataReference> _references;
    private static readonly object ReferencesLock = new();
    private static int _submissionCounter;

    /// <summary>
    /// Loads the embedded reference assemblies. Safe to call from any thread; cheap after the first call.
    /// </summary>
    public static ImmutableArray<MetadataReference> GetReferences()
    {
        lock (ReferencesLock)
        {
            if (!_references.IsDefault)
                return _references;

            var builder = ImmutableArray.CreateBuilder<MetadataReference>();
            IEnumerable<Assembly> sources = ReferenceAssemblySource is not null
                ? new[] { ReferenceAssemblySource }
                : AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic);
            foreach (Assembly source in sources)
            {
                foreach (string name in source.GetManifestResourceNames())
                {
                    if (!name.StartsWith("refs/", StringComparison.Ordinal) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using Stream stream = source.GetManifestResourceStream(name)!;
                    builder.Add(MetadataReference.CreateFromStream(stream, filePath: name));
                }
                if (builder.Count > 0)
                    break;
            }
            if (builder.Count == 0)
                throw new InvalidOperationException("No embedded script reference assemblies (refs/*.dll) were found.");
            _references = builder.ToImmutable();
            return _references;
        }
    }

    /// <summary>
    /// Compiles a script, returning diagnostics (errors and warnings) without running it.
    /// </summary>
    public static ImmutableArray<Diagnostic> Lint(string code, string scriptPath)
    {
        CSharpCompilation compilation = CreateCompilation(code, scriptPath);
        return compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToImmutableArray();
    }

    /// <summary>
    /// Compiles and runs a script with the given globals object.
    /// </summary>
    /// <exception cref="ScriptCompilationException">The script has compile errors.</exception>
    public static async Task<object> RunAsync(string code, string scriptPath, IScriptInterface globals, CancellationToken cancellationToken = default)
    {
        CSharpCompilation compilation = CreateCompilation(code, scriptPath);

        using MemoryStream peStream = new();
        using MemoryStream pdbStream = new();
        EmitResult result = compilation.Emit(peStream, pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            throw new ScriptCompilationException(result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray());
        }

        Assembly assembly = Assembly.Load(peStream.ToArray(), pdbStream.ToArray());

        IMethodSymbol entryPoint = compilation.GetEntryPoint(cancellationToken)
            ?? throw new InvalidOperationException("Script has no entry point.");
        string ns = entryPoint.ContainingNamespace.IsGlobalNamespace ? "" : entryPoint.ContainingNamespace.MetadataName + ".";
        Type entryType = assembly.GetType(ns + entryPoint.ContainingType.MetadataName, throwOnError: true)!;
        MethodInfo factoryMethod = entryType.GetMethod(entryPoint.MetadataName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var factory = factoryMethod.CreateDelegate<Func<object[], Task<object>>>();

        // Slot 0 holds the globals object; slot 1 is filled in by the submission itself.
        object[] submissionStates = new object[2];
        submissionStates[0] = globals;
        return await factory(submissionStates).ConfigureAwait(false);
    }

    private static CSharpCompilation CreateCompilation(string code, string scriptPath)
    {
        scriptPath ??= "";
        string baseDirectory = string.IsNullOrEmpty(scriptPath)
            ? DefaultBaseDirectory
            : Path.GetDirectoryName(scriptPath);

        CSharpParseOptions parseOptions = new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Script);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(SourceText.From(code, Encoding.UTF8), parseOptions, scriptPath);

        CSharpCompilationOptions options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: DefaultImports,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true)
            .WithSourceReferenceResolver(new SourceFileResolver(ImmutableArray<string>.Empty, baseDirectory))
            .WithMetadataImportOptions(MetadataImportOptions.Public);

        int id = Interlocked.Increment(ref _submissionCounter);
        return CSharpCompilation.CreateScriptCompilation(
            "UMTScript" + id,
            tree,
            GetReferences(),
            options,
            previousScriptCompilation: null,
            returnType: null, // object; resolved against the reference assemblies
            globalsType: typeof(IScriptInterface));
    }
}
