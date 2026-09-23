using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// Decompile / disassemble / compile / assemble helpers shared by the code editor and scripts.
/// These follow the desktop UndertaleModTool code editor.
/// </summary>
public static class CodeHelper
{
    public static string Decompile(UndertaleData data, UndertaleCode code, GlobalDecompileContext context = null, IDecompileSettings settings = null)
    {
        if (code is null)
            return "";
        if (code.ParentEntry is not null)
            return $"// This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name.Content}\", decompile that instead.";

        try
        {
            context ??= ReferenceEquals(data, DataSession.Data) ? DataSession.DecompileContext : new GlobalDecompileContext(data);
            return new DecompileContext(context, code, settings ?? data.ToolInfo.DecompilerSettings).DecompileToString();
        }
        catch (Exception e)
        {
            return "/*\nDECOMPILER FAILED!\n\n" + e + "\n*/";
        }
    }

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
    /// Compiles GML source into a code entry. Returns null on success, or an error message.
    /// </summary>
    public static string Compile(UndertaleData data, UndertaleCode code, string source, Action<Action> mainThreadAction)
    {
        try
        {
            CompileGroup group = new(data) { MainThreadAction = mainThreadAction };
            group.QueueCodeReplace(code, source);
            CompileResult result = group.Compile();
            if (!result.Successful)
                return result.PrintAllErrors(false);
        }
        catch (Exception e)
        {
            return e.ToString();
        }
        DataSession.InvalidateDecompileContext();
        DataSession.IsModified = true;
        return null;
    }

    /// <summary>
    /// Assembles bytecode assembly into a code entry. Returns null on success, or an error message.
    /// </summary>
    public static string Assemble(UndertaleData data, UndertaleCode code, string source, Action<Action> mainThreadAction)
    {
        try
        {
            var instructions = Assembler.Assemble(source, data, mainThreadAction);
            code.Replace(instructions);
        }
        catch (Exception e)
        {
            return e.ToString();
        }
        DataSession.InvalidateDecompileContext();
        DataSession.IsModified = true;
        return null;
    }
}
