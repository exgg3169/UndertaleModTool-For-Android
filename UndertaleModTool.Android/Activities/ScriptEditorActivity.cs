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
/// Edits a .csx script file (or a scratch buffer) and runs it.
/// </summary>
[Activity(Label = "Script editor", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden,
          WindowSoftInputMode = SoftInput.AdjustResize)]
public class ScriptEditorActivity : BaseActivity
{
    public const string ExtraPath = "path";

    public const string Template =
        "// UndertaleModTool C# script. Same API as the desktop version (IScriptInterface).\n" +
        "EnsureDataLoaded();\n\n" +
        "ScriptMessage($\"{Data.GeneralInfo.DisplayName.Content} has {Data.Code.Count} code entries and {Data.Strings.Count} strings.\");\n";

    private static string _scratch = Template;

    private string _path;
    private EditText _editor;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.SetDisplayHomeAsUpEnabled(true);
        _path = Intent!.GetStringExtra(ExtraPath);
        Title = _path is null ? "Run C# code" : Path.GetFileName(_path);

        string text = _path is null ? _scratch : File.ReadAllText(_path, Encoding.UTF8);
        _editor = UiHelper.MonospaceEditor(this, text);
        int p = this.Dp(8);
        _editor.SetPadding(p, p, p, p);
        ScrollView scroll = new(this) { FillViewport = true };
        scroll.AddView(_editor);
        SetContentView(scroll);
        UiHelper.FitSystemWindows(scroll);
    }

    private bool IsBundled => _path is not null && _path.StartsWith(StorageHelper.ScriptsDirectory, StringComparison.Ordinal);

    private void SaveBuffer()
    {
        if (_path is null)
            _scratch = _editor.Text;
        else
            File.WriteAllText(_path, _editor.Text, Encoding.UTF8);
    }

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        menu.Add(0, 1, 0, "Run")!.SetShowAsAction(ShowAsAction.Always);
        if (_path is not null)
            menu.Add(0, 2, 1, "Save");
        menu.Add(0, 3, 2, "Check for errors");
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        switch (item.ItemId)
        {
            case 1:
                SaveBuffer();
                Intent intent = new(this, typeof(ScriptConsoleActivity));
                if (_path is not null)
                    intent.PutExtra(ScriptConsoleActivity.ExtraScriptPath, _path);
                else
                    intent.PutExtra(ScriptConsoleActivity.ExtraCodeHandle, DataSession.Park(_editor.Text));
                StartActivity(intent);
                return true;
            case 2:
                SaveBuffer();
                UiHelper.Toast(this, IsBundled ? "Saved (bundled scripts are reset when the app is updated)" : "Saved");
                return true;
            case 3:
                string code = _editor.Text;
                UiHelper.RunWithProgress(this, "Checking", _ => ScriptCompiler.Lint(code, _path), diagnostics =>
                {
                    if (diagnostics.Length == 0)
                        UiHelper.ShowMessage(this, "No problems", "The script compiles without errors or warnings.");
                    else
                        UiHelper.ShowLongText(this, $"{diagnostics.Length} problem(s)", string.Join("\n\n", diagnostics));
                });
                return true;
        }
        return base.OnOptionsItemSelected(item);
    }

    protected override void OnPause()
    {
        base.OnPause();
        if (_path is null)
            _scratch = _editor.Text;
    }
}
