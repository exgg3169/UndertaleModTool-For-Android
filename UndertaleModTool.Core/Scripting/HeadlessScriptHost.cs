using System.Text;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Scripting;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Data;

namespace UndertaleModTool.Core.Scripting;

/// <summary>
/// Runs UTMT scripts without a UI (for the MCP server): messages go to a log, questions get a
/// fixed answer, text inputs return their default value (or a provided answer), and folder/file
/// prompts use the host's work directory.
/// </summary>
public sealed class HeadlessScriptHost : IScriptInterface
{
    private readonly IUmtHost _host;
    private readonly StringBuilder _log = new();
    private readonly Queue<string> _inputs;
    private int _progress;

    /// <param name="host">The app.</param>
    /// <param name="questionAnswer">Answer to every ScriptQuestion.</param>
    /// <param name="inputs">Answers for text inputs / file prompts, used in order; then defaults.</param>
    public HeadlessScriptHost(IUmtHost host, bool questionAnswer = true, IEnumerable<string> inputs = null)
    {
        _host = host;
        QuestionAnswer = questionAnswer;
        _inputs = new Queue<string>(inputs ?? Enumerable.Empty<string>());
        OutputDirectory = Path.Combine(host.WorkDirectory, "Script output", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
    }

    public bool QuestionAnswer { get; }

    /// <summary>Directory returned by PromptChooseDirectory (created on first use).</summary>
    public string OutputDirectory { get; }

    public string Log
    {
        get
        {
            lock (_log)
                return _log.ToString();
        }
    }

    private void Write(string text)
    {
        lock (_log)
        {
            _log.AppendLine(text);
            if (_log.Length > 1_000_000)
                _log.Remove(0, _log.Length - 800_000);
        }
    }

    #region Running

    public async Task<(bool Success, object Result)> RunAsync(string code, string scriptPath)
    {
        string previous = ScriptPath;
        ScriptPath = scriptPath;
        object result = null;
        try
        {
            result = await ScriptCompiler.RunAsync(code, scriptPath, this).ConfigureAwait(false);
            ScriptExecutionSuccess = true;
            ScriptErrorMessage = "";
            ScriptErrorType = "";
        }
        catch (ScriptCancelledException)
        {
            ScriptExecutionSuccess = true;
            Write("Script was cancelled.");
        }
        catch (ScriptCompilationException e)
        {
            ScriptExecutionSuccess = false;
            ScriptErrorType = "Compilation error";
            ScriptErrorMessage = e.Message;
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
            ScriptPath = previous;
        }
        return (ScriptExecutionSuccess, result);
    }

    #endregion

    #region IScriptInterface state

    public UndertaleData Data => _host.Data;
    public ProjectContext Project => null;
    public string FilePath => _host.DataName;
    public string ScriptPath { get; private set; }
    public object Highlighted => null;
    public object Selected { get; private set; }
    public bool CanSave => _host.Data is not null;
    public bool ScriptExecutionSuccess { get; private set; }
    public string ScriptErrorMessage { get; private set; } = "";
    public string ExePath => Path.TrimEndingDirectorySeparator(_host.ExePath) + Path.DirectorySeparatorChar;
    public string ScriptErrorType { get; private set; } = "";
    public bool IsAppClosed => false;
    public Action<Action> MainThreadAction => _host.RunOnMainThread;

    #endregion

    #region Messages & prompts

    public void EnsureDataLoaded()
    {
        if (Data is null)
            throw new ScriptException("No data file is currently loaded!");
    }

    public bool MakeNewDataFile()
    {
        Write("MakeNewDataFile() is not supported from the MCP server; open or create the file in the app.");
        return false;
    }

    public void ScriptMessage(string message) => Write("[message] " + message);
    public void ScriptWarning(string message) => Write("[warning] " + message);
    public void SetUMTConsoleText(string message) => Write(message);

    public bool ScriptQuestion(string message)
    {
        Write($"[question] {message} -> {(QuestionAnswer ? "Yes" : "No")}");
        return QuestionAnswer;
    }

    public void ScriptError(string error, string title = "Error", bool SetConsoleText = true) => Write($"[{title}] {error}");

    public void ScriptOpenURL(string url) => Write("[open url] " + url);

    public bool RunUMTScript(string path)
    {
        if (!File.Exists(path))
        {
            Write($"[error] Script not found: {path}");
            return false;
        }
        Write($"Running nested script {Path.GetFileName(path)}...");
        return RunAsync(File.ReadAllText(path, Encoding.UTF8), path).GetAwaiter().GetResult().Success;
    }

    public bool LintUMTScript(string path)
    {
        var errors = ScriptCompiler.Lint(File.ReadAllText(path, Encoding.UTF8), path)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        if (errors.Count == 0)
            return true;
        ScriptErrorType = "Compilation error";
        ScriptErrorMessage = string.Join("\n", errors);
        return false;
    }

    public void InitializeScriptDialog()
    {
    }

    private string NextInput(string what, string fallback)
    {
        string value = _inputs.Count > 0 ? _inputs.Dequeue() : fallback;
        Write($"[input] {what} -> {value ?? "(cancel)"}");
        return value;
    }

    public string ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose)
        => NextInput($"{title}: {label}", defaultInput);

    public string SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true)
        => NextInput($"{title}: {label}", defaultValue);

    public void SimpleTextOutput(string title, string label, string message, bool allowMultiline)
        => Write($"[{title}] {label}\n{message}");

    public Task ClickableSearchOutput(string title, string query, int resultsCount, IOrderedEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> resultsDict, bool showInDecompiledView, IOrderedEnumerable<string> failedList = null)
        => ClickableSearchOutput(title, query, resultsCount, resultsDict.ToDictionary(k => k.Key, k => k.Value), showInDecompiledView, failedList);

    public Task ClickableSearchOutput(string title, string query, int resultsCount, IDictionary<string, List<(int lineNum, string codeLine)>> resultsDict, bool showInDecompiledView, IEnumerable<string> failedList = null)
    {
        StringBuilder sb = new($"[{title}] {resultsCount} result(s) for \"{query}\"\n");
        foreach (var (code, lines) in resultsDict)
        {
            sb.AppendLine(code);
            foreach (var (lineNum, line) in lines)
                sb.AppendLine($"  {lineNum}: {line}");
        }
        if (failedList is not null)
            sb.AppendLine("Failed to decompile: " + string.Join(", ", failedList));
        Write(sb.ToString());
        return Task.CompletedTask;
    }

    public void SetFinishedMessage(bool isFinishedMessageEnabled)
    {
    }

    public void ChangeSelection(object newSelection, bool inNewTab = false)
    {
        Selected = newSelection;
        Write("[selected] " + ObjectInspector.NameOf(newSelection));
    }

    public string PromptChooseDirectory()
    {
        string path = NextInput("PromptChooseDirectory", OutputDirectory);
        if (path is null)
            return null;
        Directory.CreateDirectory(path);
        return Path.TrimEndingDirectorySeparator(path) + "/";
    }

    public string PromptLoadFile(string defaultExt, string filter) => NextInput($"PromptLoadFile ({filter})", null);

    public string PromptSaveFile(string defaultExt, string filter)
    {
        Directory.CreateDirectory(OutputDirectory);
        return NextInput($"PromptSaveFile ({filter})", Path.Combine(OutputDirectory, "output." + (defaultExt ?? "bin").TrimStart('.')));
    }

    #endregion

    #region Code

    public string GetDecompiledText(string codeName, GlobalDecompileContext context = null, IDecompileSettings settings = null)
        => GetDecompiledText(Data.Code.ByName(codeName), context, settings);

    public string GetDecompiledText(UndertaleCode code, GlobalDecompileContext context = null, IDecompileSettings settings = null)
        => CodeTools.Decompile(Data, code, context ?? _host.DecompileContext, settings);

    public string GetDisassemblyText(string codeName) => GetDisassemblyText(Data.Code.ByName(codeName));

    public string GetDisassemblyText(UndertaleCode code) => CodeTools.Disassemble(Data, code);

    #endregion

    #region Progress (logged sparsely)

    public void UpdateProgressBar(string message, string status, double progressValue, double maxValue)
    {
        _progress = (int)progressValue;
        if (!string.IsNullOrEmpty(message))
            Write($"[progress] {message} {status}".TrimEnd());
    }

    public void SetProgressBar(string message, string status, double progressValue, double maxValue)
        => UpdateProgressBar(message, status, progressValue, maxValue);

    public void SetProgressBar()
    {
    }

    public void UpdateProgressValue(double progressValue) => _progress = (int)progressValue;
    public void UpdateProgressStatus(string status)
    {
    }

    public void AddProgress(int amount) => Interlocked.Add(ref _progress, amount);
    public void IncrementProgress() => Interlocked.Increment(ref _progress);
    public void AddProgressParallel(int amount) => Interlocked.Add(ref _progress, amount);
    public void IncrementProgressParallel() => Interlocked.Increment(ref _progress);
    public int GetProgress() => _progress;
    public void SetProgress(int value) => _progress = value;
    public void HideProgressBar()
    {
    }

    public void EnableUI()
    {
    }

    public void StartProgressBarUpdater()
    {
    }

    public Task StopProgressBarUpdater() => Task.CompletedTask;

    #endregion
}
