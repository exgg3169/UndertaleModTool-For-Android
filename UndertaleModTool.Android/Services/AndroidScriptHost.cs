using System.Text;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Scripting;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Scripting;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// UI operations a running script may need. Blocking methods are called from the script
/// thread and must not return before the user has answered.
/// </summary>
public interface IScriptUi
{
    void Log(string text);
    void ShowMessage(string title, string message);
    bool Ask(string title, string message);
    string Input(string title, string label, string defaultValue, bool multiline, string okText, string cancelText);
    void ShowText(string title, string label, string text);
    string PickPath(FileBrowserMode mode, string defaultExt);
    void OpenUrl(string url);
    void ShowSearchResults(string title, string query, int resultsCount,
                           IEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> results,
                           IEnumerable<string> failedList);
    void Navigate(object target);
    void RunOnUiThreadAndWait(Action action);
}

/// <summary>
/// Android implementation of <see cref="IScriptInterface"/>, the API that UndertaleModTool
/// scripts (.csx) are written against.
/// </summary>
public sealed class AndroidScriptHost : IScriptInterface
{
    private readonly IScriptUi _ui;

    public AndroidScriptHost(IScriptUi ui)
    {
        _ui = ui;
    }

    #region State

    public UndertaleData Data => DataSession.Data;
    public ProjectContext Project => null;
    public string FilePath => DataSession.FilePath;
    public string ScriptPath { get; set; }
    public object Highlighted => DataSession.Selected;
    public object Selected => DataSession.Selected;
    public bool CanSave => DataSession.Data is not null;
    public bool ScriptExecutionSuccess { get; private set; }
    public string ScriptErrorMessage { get; private set; } = "";
    public string ScriptErrorType { get; private set; } = "";

    /// <summary>
    /// Scripts use this as a base directory for resources shipped next to the tool; on Android
    /// that's the directory where the bundled scripts are extracted.
    /// </summary>
    public string ExePath => StorageHelper.LibDataDirectory + Path.DirectorySeparatorChar;

    public bool IsAppClosed { get; set; }

    public Action<Action> MainThreadAction => action => _ui.RunOnUiThreadAndWait(action);

    /// <summary>Whether the "script finished" message should be shown (scripts can turn it off).</summary>
    public bool FinishedMessageEnabled { get; private set; } = true;

    #endregion

    #region Running scripts

    /// <summary>
    /// Runs a script, recording success/failure in <see cref="ScriptExecutionSuccess"/> and friends.
    /// </summary>
    public async Task<bool> RunScriptAsync(string code, string scriptPath)
    {
        string previousPath = ScriptPath;
        ScriptPath = scriptPath;
        try
        {
            await ScriptCompiler.RunAsync(code, scriptPath, this).ConfigureAwait(false);
            ScriptExecutionSuccess = true;
            ScriptErrorMessage = "";
            ScriptErrorType = "";
        }
        catch (ScriptCancelledException)
        {
            ScriptExecutionSuccess = true;
            ScriptErrorMessage = "";
            ScriptErrorType = "";
            _ui.Log("Script was cancelled.");
        }
        catch (ScriptCompilationException e)
        {
            ScriptExecutionSuccess = false;
            ScriptErrorMessage = e.Message;
            ScriptErrorType = "Compilation error";
        }
        catch (Exception e)
        {
            ScriptExecutionSuccess = false;
            ScriptErrorType = "Exception";
            try
            {
                ScriptErrorMessage = ScriptingUtil.PrettifyException(e);
            }
            catch
            {
                ScriptErrorMessage = e.ToString();
            }
        }
        finally
        {
            ScriptPath = previousPath;
            HideProgressBar();
        }

        if (!ScriptExecutionSuccess)
            _ui.Log($"{ScriptErrorType}:\n{ScriptErrorMessage}");
        return ScriptExecutionSuccess;
    }

    public bool RunUMTScript(string path)
    {
        if (!File.Exists(path))
        {
            ScriptError($"Script \"{path}\" does not exist.");
            return false;
        }
        _ui.Log($"Running nested script {Path.GetFileName(path)}...");
        return RunScriptAsync(File.ReadAllText(path, Encoding.UTF8), path).GetAwaiter().GetResult();
    }

    public bool LintUMTScript(string path)
    {
        if (!File.Exists(path))
        {
            ScriptError($"Script \"{path}\" does not exist.");
            return false;
        }
        var diagnostics = ScriptCompiler.Lint(File.ReadAllText(path, Encoding.UTF8), path);
        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        if (errors.Count == 0)
            return true;
        ScriptErrorType = "Compilation error";
        ScriptErrorMessage = string.Join("\n", errors);
        return false;
    }

    #endregion

    #region Messages & dialogs

    public void EnsureDataLoaded()
    {
        if (Data is null)
            throw new ScriptException("No data file is currently loaded!");
    }

    public bool MakeNewDataFile()
    {
        if (DataSession.Data is not null && DataSession.IsModified &&
            !ScriptQuestion("Warning: you currently have a data file open with unsaved changes. Replace it with a new, empty one?"))
        {
            return false;
        }
        _ui.RunOnUiThreadAndWait(() =>
            DataSession.SetData(UndertaleData.CreateNew(), null, null, "data.win (new)"));
        return true;
    }

    public void ScriptMessage(string message)
    {
        _ui.Log(message);
        _ui.ShowMessage("Script message", message);
    }

    public void ScriptWarning(string message)
    {
        _ui.Log("WARNING: " + message);
        _ui.ShowMessage("Script warning", message);
    }

    public void SetUMTConsoleText(string message) => _ui.Log(message);

    public bool ScriptQuestion(string message) => _ui.Ask("Script question", message);

    public void ScriptError(string error, string title = "Error", bool SetConsoleText = true)
    {
        if (SetConsoleText)
            _ui.Log($"{title}: {error}");
        _ui.ShowMessage(title, error);
    }

    public void ScriptOpenURL(string url) => _ui.OpenUrl(url);

    public void InitializeScriptDialog()
    {
        // The console screen already contains the progress UI.
    }

    public string ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose)
        => _ui.Input(title, label, defaultInput, isMultiline, submitText, cancelText);

    public string SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true)
        => _ui.Input(title, label, defaultValue, allowMultiline, "OK", "Cancel");

    public void SimpleTextOutput(string title, string label, string message, bool allowMultiline)
        => _ui.ShowText(title, label, message);

    public Task ClickableSearchOutput(string title, string query, int resultsCount, IOrderedEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> resultsDict, bool showInDecompiledView, IOrderedEnumerable<string> failedList = null)
    {
        _ui.ShowSearchResults(title, query, resultsCount, resultsDict.ToList(), failedList?.ToList());
        return Task.CompletedTask;
    }

    public Task ClickableSearchOutput(string title, string query, int resultsCount, IDictionary<string, List<(int lineNum, string codeLine)>> resultsDict, bool showInDecompiledView, IEnumerable<string> failedList = null)
    {
        _ui.ShowSearchResults(title, query, resultsCount, resultsDict.ToList(), failedList?.ToList());
        return Task.CompletedTask;
    }

    public void SetFinishedMessage(bool isFinishedMessageEnabled) => FinishedMessageEnabled = isFinishedMessageEnabled;

    public void EnableUI()
    {
    }

    public void ChangeSelection(object newSelection, bool inNewTab = false)
    {
        DataSession.Selected = newSelection;
        _ui.Navigate(newSelection);
    }

    public string PromptChooseDirectory()
    {
        string path = _ui.PickPath(FileBrowserMode.Directory, null);
        // Same as desktop UMT: directories are returned with a trailing separator.
        return path is null ? null : Path.TrimEndingDirectorySeparator(path) + "/";
    }

    public string PromptLoadFile(string defaultExt, string filter) => _ui.PickPath(FileBrowserMode.OpenFile, defaultExt);

    public string PromptSaveFile(string defaultExt, string filter) => _ui.PickPath(FileBrowserMode.SaveFile, defaultExt);

    #endregion

    #region Decompiler

    public string GetDecompiledText(string codeName, GlobalDecompileContext context = null, IDecompileSettings settings = null)
        => GetDecompiledText(Data.Code.ByName(codeName), context, settings);

    public string GetDecompiledText(UndertaleCode code, GlobalDecompileContext context = null, IDecompileSettings settings = null)
        => CodeHelper.Decompile(Data, code, context, settings);

    public string GetDisassemblyText(string codeName) => GetDisassemblyText(Data.Code.ByName(codeName));

    public string GetDisassemblyText(UndertaleCode code) => CodeHelper.Disassemble(Data, code);

    #endregion

    #region Progress

    private readonly object _progressLock = new();
    private int _progressValue;

    public bool ProgressVisible { get; private set; }
    public string ProgressMessage { get; private set; } = "";
    public string ProgressStatus { get; private set; } = "";
    public double ProgressMax { get; private set; }
    public double ProgressCurrent => _progressValue;

    public void UpdateProgressBar(string message, string status, double progressValue, double maxValue)
    {
        lock (_progressLock)
        {
            if (message is not null)
                ProgressMessage = message;
            if (status is not null)
                ProgressStatus = status;
            _progressValue = (int)progressValue;
            ProgressMax = maxValue;
            ProgressVisible = true;
        }
    }

    public void SetProgressBar(string message, string status, double progressValue, double maxValue)
        => UpdateProgressBar(message, status, progressValue, maxValue);

    public void SetProgressBar() => ProgressVisible = true;

    public void UpdateProgressValue(double progressValue) => Interlocked.Exchange(ref _progressValue, (int)progressValue);

    public void UpdateProgressStatus(string status) => ProgressStatus = status ?? "";

    public void AddProgress(int amount) => Interlocked.Add(ref _progressValue, amount);

    public void IncrementProgress() => Interlocked.Increment(ref _progressValue);

    public void AddProgressParallel(int amount) => Interlocked.Add(ref _progressValue, amount);

    public void IncrementProgressParallel() => Interlocked.Increment(ref _progressValue);

    public int GetProgress() => Volatile.Read(ref _progressValue);

    public void SetProgress(int value) => Interlocked.Exchange(ref _progressValue, value);

    public void HideProgressBar() => ProgressVisible = false;

    public void StartProgressBarUpdater()
    {
        // The console activity polls the progress state continuously.
        ProgressVisible = true;
    }

    public Task StopProgressBarUpdater() => Task.CompletedTask;

    #endregion
}
