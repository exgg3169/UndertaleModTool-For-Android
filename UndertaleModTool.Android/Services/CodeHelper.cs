using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModTool.Core.Data;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// Code editing for the UI: <see cref="CodeTools"/> plus session bookkeeping (shared decompile
/// context, modified flag).
/// </summary>
public static class CodeHelper
{
    public static string Decompile(UndertaleData data, UndertaleCode code, GlobalDecompileContext context = null, IDecompileSettings settings = null)
    {
        context ??= ReferenceEquals(data, DataSession.Data) ? DataSession.DecompileContext : null;
        return CodeTools.Decompile(data, code, context, settings);
    }

    public static string Disassemble(UndertaleData data, UndertaleCode code) => CodeTools.Disassemble(data, code);

    /// <summary>Compiles GML into a code entry. Returns null on success, or an error message.</summary>
    public static string Compile(UndertaleData data, UndertaleCode code, string source, Action<Action> mainThreadAction)
        => AfterEdit(CodeTools.CompileReplace(data, code, source, mainThreadAction));

    /// <summary>Assembles bytecode assembly into a code entry. Returns null on success, or an error message.</summary>
    public static string Assemble(UndertaleData data, UndertaleCode code, string source, Action<Action> mainThreadAction)
        => AfterEdit(CodeTools.Assemble(data, code, source, mainThreadAction));

    private static string AfterEdit(string error)
    {
        if (error is null)
        {
            DataSession.InvalidateDecompileContext();
            DataSession.IsModified = true;
        }
        return error;
    }
}
