using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace UndertaleModTool.Core.Data;

/// <summary>
/// Decompiling, disassembling, compiling and searching code, like the desktop code editor.
/// </summary>
public static class CodeTools
{
    public static string Decompile(UndertaleData data, UndertaleCode code, GlobalDecompileContext context = null, IDecompileSettings settings = null)
    {
        if (code is null)
            return "";
        if (code.ParentEntry is not null)
            return $"// This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name.Content}\", decompile that instead.";
        try
        {
            context ??= new GlobalDecompileContext(data);
            return new DecompileContext(context, code, settings ?? data.ToolInfo.DecompilerSettings).DecompileToString();
        }
        catch (Exception e)
        {
            return "/*\nDECOMPILER FAILED!\n\n" + e + "\n*/";
        }
    }

    public static bool IsDecompileFailure(string text) => text.StartsWith("/*\nDECOMPILER FAILED!", StringComparison.Ordinal);

    public static string Disassemble(UndertaleData data, UndertaleCode code)
    {
        if (code is null)
            return "";
        if (code.ParentEntry is not null)
            return $"; This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name.Content}\", disassemble that instead.";
        try
        {
            return code.Disassemble(data.Variables, data.CodeLocals?.For(code), data.CodeLocals is null);
        }
        catch (Exception e)
        {
            return "/*\nDISASSEMBLY FAILED!\n\n" + e + "\n*/";
        }
    }

    /// <summary>
    /// Compiles GML into a code entry (replacing its code). Returns null on success, or the errors.
    /// </summary>
    public static string CompileReplace(UndertaleData data, UndertaleCode code, string source, Action<Action> mainThreadAction = null)
    {
        try
        {
            CompileGroup group = new(data) { MainThreadAction = mainThreadAction ?? (f => f()) };
            group.QueueCodeReplace(code, source);
            CompileResult result = group.Compile();
            return result.Successful ? null : result.PrintAllErrors(false);
        }
        catch (Exception e)
        {
            return e.ToString();
        }
    }

    public enum ImportMode
    {
        Replace,
        Append,
        Prepend,
        FindReplace,
        RegexFindReplace,
    }

    /// <summary>
    /// Imports GML into a code entry by name, creating the entry (and linking object events /
    /// scripts) if it doesn't exist, like UTMT's ImportGML. Returns null on success, or the errors.
    /// </summary>
    public static string Import(UndertaleData data, string codeName, ImportMode mode, string gml, string search = null,
                                Action<Action> mainThreadAction = null)
    {
        try
        {
            CodeImportGroup group = new(data) { MainThreadAction = mainThreadAction ?? (f => f()), ThrowOnNoOpFindReplace = true };
            switch (mode)
            {
                case ImportMode.Replace:
                    group.QueueReplace(codeName, gml);
                    break;
                case ImportMode.Append:
                    group.QueueAppend(codeName, gml);
                    break;
                case ImportMode.Prepend:
                    group.QueuePrepend(codeName, gml);
                    break;
                case ImportMode.FindReplace:
                    group.QueueFindReplace(codeName, search ?? throw new ArgumentException("search is required"), gml);
                    break;
                case ImportMode.RegexFindReplace:
                    group.QueueRegexFindReplace(codeName, search ?? throw new ArgumentException("search is required"), gml);
                    break;
            }
            CompileResult result = group.Import(throwOnFailedCompile: false);
            return result.Successful ? null : result.PrintAllErrors(true);
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Assembles bytecode assembly into a code entry. Returns null on success, or the error.
    /// </summary>
    public static string Assemble(UndertaleData data, UndertaleCode code, string source, Action<Action> mainThreadAction = null)
    {
        try
        {
            code.Replace(Assembler.Assemble(source, data, mainThreadAction));
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public sealed record SearchResult(List<(string Code, int Line, string Text)> Matches, List<string> Failed, int Searched);

    /// <summary>Searches all decompiled code (in parallel).</summary>
    public static SearchResult Search(UndertaleData data, string query, bool regex, bool caseSensitive,
                                      GlobalDecompileContext context = null, int maxResults = int.MaxValue,
                                      Action<int, int> progress = null)
    {
        Regex re = regex ? new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) : null;
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var codes = data.Code?.Where(c => c is not null && c.ParentEntry is null).ToList() ?? new List<UndertaleCode>();
        context ??= new GlobalDecompileContext(data);

        ConcurrentBag<(string, int, string)> matches = new();
        ConcurrentBag<string> failed = new();
        int done = 0;
        Parallel.ForEach(codes, code =>
        {
            string text = Decompile(data, code, context);
            string name = code.Name?.Content ?? "?";
            if (IsDecompileFailure(text))
                failed.Add(name);
            int lineNum = 0;
            foreach (string line in text.Split('\n'))
            {
                lineNum++;
                if (re?.IsMatch(line) ?? line.Contains(query, comparison))
                    matches.Add((name, lineNum, line.Trim()));
            }
            progress?.Invoke(Interlocked.Increment(ref done), codes.Count);
        });

        return new SearchResult(
            matches.OrderBy(m => m.Item1, StringComparer.Ordinal).ThenBy(m => m.Item2).Take(maxResults).ToList(),
            failed.OrderBy(f => f, StringComparer.Ordinal).ToList(),
            codes.Count);
    }
}
