using UndertaleModLib;
using UndertaleModLib.Decompiler;

namespace UndertaleModTool.Core;

/// <summary>
/// What the platform-independent tools (MCP server, headless scripts) need from the app.
/// </summary>
public interface IUmtHost
{
    /// <summary>The loaded data file, or null.</summary>
    UndertaleData Data { get; }

    /// <summary>User-visible name of the loaded file.</summary>
    string DataName { get; }

    /// <summary>Whether the data has unsaved changes.</summary>
    bool IsModified { get; }

    /// <summary>Directory where tools may read/write files (exports, script output).</summary>
    string WorkDirectory { get; }

    /// <summary>Directory of the bundled scripts.</summary>
    string ScriptsDirectory { get; }

    /// <summary>Base directory used as ExePath by scripts (contains "Scripts" and "GameSpecificData").</summary>
    string ExePath { get; }

    /// <summary>Decompile context shared for the loaded data.</summary>
    GlobalDecompileContext DecompileContext { get; }

    /// <summary>Loads a data file from a file system path, replacing the current one.</summary>
    void LoadDataFile(string path);

    /// <summary>
    /// Saves the data. A null path means "where it was opened from". Returns a description of where it was saved.
    /// </summary>
    string SaveDataFile(string path);

    /// <summary>Called after the data was modified.</summary>
    void MarkModified(bool codeChanged = false);

    /// <summary>Runs an action on the UI thread and waits for it.</summary>
    void RunOnMainThread(Action action);
}
