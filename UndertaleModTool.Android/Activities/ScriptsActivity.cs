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
/// Lists bundled and user scripts. Tap to run, long-press to view/edit.
/// </summary>
[Activity(Label = "Scripts", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public class ScriptsActivity : BaseActivity
{
    private ListView _list;
    private readonly List<(string Label, string Path)> _rows = new();

    public static string UserScriptsDirectory => System.IO.Path.Combine(StorageHelper.WorkDirectory, "Scripts");

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.SetDisplayHomeAsUpEnabled(true);
        Directory.CreateDirectory(UserScriptsDirectory);

        LinearLayout root = UiHelper.VerticalLayout(this, 0);
        TextView hint = UiHelper.Label(this,
            "Tap a script to run it, long-press to view or edit it. " +
            $"Put your own .csx scripts in {StorageHelper.Pretty(UserScriptsDirectory)}.", 12);
        int p = this.Dp(12);
        hint.SetPadding(p, p, p, p);
        root.AddView(hint);

        _list = new ListView(this) { FastScrollEnabled = true };
        _list.ItemClick += (_, e) =>
        {
            string path = _rows[e.Position].Path;
            if (path is not null)
                ConfirmRun(path);
        };
        _list.ItemLongClick += (_, e) =>
        {
            string path = _rows[e.Position].Path;
            if (path is not null)
                OpenEditor(path);
        };
        root.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        SetContentView(root);
        UiHelper.FitSystemWindows(root);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Reload();
    }

    private void Reload()
    {
        _rows.Clear();
        AddFolder("My scripts", UserScriptsDirectory);
        if (Directory.Exists(StorageHelper.ScriptsDirectory))
        {
            foreach (string dir in Directory.GetDirectories(StorageHelper.ScriptsDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                AddFolder(System.IO.Path.GetFileName(dir), dir);
        }
        _list.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1, _rows.Select(r => r.Label).ToList());
    }

    private void AddFolder(string title, string dir)
    {
        var scripts = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.csx", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        _rows.Add(($"━━ {title} ({scripts.Count}) ━━", null));
        foreach (string script in scripts)
            _rows.Add(("   " + System.IO.Path.GetRelativePath(dir, script), script));
    }

    private void ConfirmRun(string path)
    {
        string message = DataSession.Data is null
            ? "No data file is loaded. Most scripts need one. Run anyway?"
            : $"Run {System.IO.Path.GetFileName(path)} on {DataSession.DisplayName}?";
        UiHelper.Confirm(this, "Run script", message, () => RunScript(this, path), yes: "Run", no: "Cancel");
    }

    public static void RunScript(Context context, string path)
    {
        Intent intent = new(context, typeof(ScriptConsoleActivity));
        intent.PutExtra(ScriptConsoleActivity.ExtraScriptPath, path);
        context.StartActivity(intent);
    }

    private void OpenEditor(string path)
    {
        Intent intent = new(this, typeof(ScriptEditorActivity));
        intent.PutExtra(ScriptEditorActivity.ExtraPath, path);
        StartActivity(intent);
    }

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        menu.Add(0, 1, 0, "Run C# code...")!.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, 2, 1, "Import .csx from device...");
        menu.Add(0, 3, 2, "New script...");
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        switch (item.ItemId)
        {
            case 1:
                StartActivity(new Intent(this, typeof(ScriptEditorActivity)));
                return true;
            case 2:
                Intent intent = new(Intent.ActionOpenDocument);
                intent.AddCategory(Intent.CategoryOpenable);
                intent.SetType("*/*");
                StartForResult(intent, (result, data) =>
                {
                    if (result != Result.Ok || data?.Data is null)
                        return;
                    try
                    {
                        string path = StorageHelper.CopyUriToDirectory(this, data.Data, UserScriptsDirectory);
                        Reload();
                        ConfirmRun(path);
                    }
                    catch (Exception e)
                    {
                        UiHelper.ShowLongText(this, "Import failed", e.ToString());
                    }
                });
                return true;
            case 3:
                UiHelper.PromptText(this, "New script", "File name", "MyScript.csx", false, name =>
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return;
                    if (!name.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
                        name += ".csx";
                    string path = System.IO.Path.Combine(UserScriptsDirectory, name.Trim());
                    if (!File.Exists(path))
                        File.WriteAllText(path, ScriptEditorActivity.Template);
                    OpenEditor(path);
                });
                return true;
        }
        return base.OnOptionsItemSelected(item);
    }
}
