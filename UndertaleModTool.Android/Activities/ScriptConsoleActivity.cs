using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// Runs a C# script and shows its output, progress and dialogs.
/// </summary>
[Activity(Label = "Script", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class ScriptConsoleActivity : BaseActivity, IScriptUi
{
    public const string ExtraScriptPath = "scriptPath";
    public const string ExtraCodeHandle = "codeHandle";

    private AndroidScriptHost _host;
    private TextView _status;
    private ProgressBar _progress;
    private TextView _log;
    private ScrollView _logScroll;
    private readonly StringBuilder _logText = new();
    private bool _running;
    private Handler _handler;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.SetDisplayHomeAsUpEnabled(true);

        string scriptPath = Intent!.GetStringExtra(ExtraScriptPath);
        string code = DataSession.Fetch(Intent.GetIntExtra(ExtraCodeHandle, 0)) as string;
        if (code is null && scriptPath is not null && File.Exists(scriptPath))
            code = File.ReadAllText(scriptPath, Encoding.UTF8);
        if (code is null)
        {
            Finish();
            return;
        }
        Title = scriptPath is null ? "C# code" : Path.GetFileName(scriptPath);

        LinearLayout root = UiHelper.VerticalLayout(this, 12);
        _status = UiHelper.Label(this, "Compiling script...", 15, true);
        root.AddView(_status);
        _progress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Indeterminate = true };
        root.AddView(_progress);

        _log = UiHelper.Label(this, "", 12);
        _log.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        _log.SetTextIsSelectable(true);
        _logScroll = new ScrollView(this);
        _logScroll.AddView(_log);
        root.AddView(_logScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        SetContentView(root);
        UiHelper.FitSystemWindows(root);

        _handler = new Handler(Looper.MainLooper!);
        _host = new AndroidScriptHost(this);
        Run(code, scriptPath);
    }

    private void Run(string code, string scriptPath)
    {
        _running = true;
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        PollProgress();
        Log(DataSession.Data is null ? "Note: no data file is loaded." : $"Data file: {DataSession.DisplayName}");

        Task.Run(async () =>
        {
            bool ok = await _host.RunScriptAsync(code, scriptPath).ConfigureAwait(false);
            RunOnUiThread(() => OnFinished(ok));
        });
    }

    private void OnFinished(bool ok)
    {
        _running = false;
        Window!.ClearFlags(WindowManagerFlags.KeepScreenOn);
        _progress.Indeterminate = false;
        _progress.Max = 1;
        _progress.Progress = ok ? 1 : 0;
        if (DataSession.Data is not null)
            DataSession.IsModified = true;

        if (ok)
        {
            _status.Text = "Script finished successfully.";
            Log("Finished.");
            if (_host.FinishedMessageEnabled)
                UiHelper.Toast(this, Title + " finished successfully");
        }
        else
        {
            _status.Text = "Script failed: " + _host.ScriptErrorType;
            UiHelper.ShowLongText(this, _host.ScriptErrorType ?? "Script error", _host.ScriptErrorMessage);
        }
    }

    private void PollProgress()
    {
        if (!_running)
            return;

        if (_host.ProgressVisible)
        {
            string message = _host.ProgressMessage;
            string status = _host.ProgressStatus;
            _status.Text = string.IsNullOrEmpty(status) ? message : $"{message}\n{status}";
            if (_host.ProgressMax > 0)
            {
                _progress.Indeterminate = false;
                _progress.Max = (int)Math.Min(int.MaxValue, _host.ProgressMax);
                _progress.Progress = (int)Math.Min(_progress.Max, _host.ProgressCurrent);
                _status.Text += $" ({(int)_host.ProgressCurrent}/{(int)_host.ProgressMax})";
            }
        }
        else
        {
            _status.Text = "Running...";
            _progress.Indeterminate = true;
        }
        _handler.PostDelayed(PollProgress, 200);
    }

    public override void OnBackPressed()
    {
        if (_running)
        {
            UiHelper.Toast(this, "The script is still running.");
            return;
        }
#pragma warning disable CA1422
        base.OnBackPressed();
#pragma warning restore CA1422
    }

    #region IScriptUi

    public void Log(string text)
    {
        RunOnUiThread(() =>
        {
            _logText.AppendLine(text);
            // Keep the log bounded; some scripts print a line per resource.
            if (_logText.Length > 200_000)
                _logText.Remove(0, _logText.Length - 150_000);
            _log.Text = _logText.ToString();
            _logScroll.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
        });
    }

    private static bool OnMainThread => Looper.MyLooper() == Looper.MainLooper;

    /// <summary>
    /// Shows UI on the main thread and blocks the (script) thread until <c>complete</c> is called.
    /// </summary>
    private T WaitForUi<T>(Action<Action<T>> show, T fallback = default)
    {
        if (OnMainThread)
        {
            // Called from inside MainThreadAction; blocking here would deadlock.
            show(_ => { });
            return fallback;
        }

        T result = fallback;
        using ManualResetEventSlim done = new();
        RunOnUiThread(() =>
        {
            try
            {
                show(r =>
                {
                    result = r;
                    done.Set();
                });
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Error("UMT", e.ToString());
                done.Set();
            }
        });
        done.Wait();
        return result;
    }

    public void ShowMessage(string title, string message)
        => WaitForUi<bool>(complete => UiHelper.ShowMessage(this, title, message, () => complete(true)));

    public bool Ask(string title, string message)
        => WaitForUi<bool>(complete => UiHelper.Confirm(this, title, message, () => complete(true), () => complete(false)));

    public string Input(string title, string label, string defaultValue, bool multiline, string okText, string cancelText)
        => WaitForUi<string>(complete => UiHelper.PromptText(this, title, label, defaultValue, multiline, complete, okText, cancelText));

    public void ShowText(string title, string label, string text)
        => WaitForUi<bool>(complete => UiHelper.ShowLongText(this, title, string.IsNullOrEmpty(label) ? text : label + "\n\n" + text, () => complete(true)));

    public string PickPath(FileBrowserMode mode, string defaultExt)
        => WaitForUi<string>(complete => FileBrowserDialog.Show(this, mode, defaultExt, complete));

    public void OpenUrl(string url)
    {
        RunOnUiThread(() =>
        {
            try
            {
                StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)));
            }
            catch (Exception e)
            {
                UiHelper.Toast(this, e.Message);
            }
        });
    }

    public void ShowSearchResults(string title, string query, int resultsCount,
                                  IEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> results,
                                  IEnumerable<string> failedList)
    {
        var payload = new CodeSearchActivity.ResultSet(title, query, resultsCount, results.ToList(), failedList?.ToList());
        RunOnUiThread(() =>
        {
            Intent intent = new(this, typeof(CodeSearchActivity));
            intent.PutExtra(CodeSearchActivity.ExtraResults, DataSession.Park(payload));
            StartActivity(intent);
        });
    }

    public void Navigate(object target) => RunOnUiThread(() => Navigator.Open(this, target));

    public void RunOnUiThreadAndWait(Action action)
    {
        if (OnMainThread)
        {
            action();
            return;
        }

        Exception error = null;
        using ManualResetEventSlim done = new();
        RunOnUiThread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    #endregion
}
