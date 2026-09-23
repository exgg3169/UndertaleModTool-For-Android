using System.Collections;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using UndertaleModLib;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;

namespace UndertaleModTool.Android.Activities;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private const int RequestOpen = 1;
    private const int RequestSaveAs = 2;

    private TextView _info;
    private ListView _categories;
    private readonly List<(string Name, Func<object> Get)> _entries = new();

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        LinearLayout root = UiHelper.VerticalLayout(this, 0);
        _info = UiHelper.Label(this, "", 14);
        int p = this.Dp(16);
        _info.SetPadding(p, p, p, p);
        _info.SetTextIsSelectable(true);
        root.AddView(_info);

        _categories = new ListView(this);
        _categories.ItemClick += (_, e) =>
        {
            var (name, get) = _entries[e.Position];
            Navigator.Open(this, get(), name);
        };
        root.AddView(_categories, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        SetContentView(root);
        UiHelper.FitSystemWindows(root);
        DataSession.DataChanged += OnDataChanged;
        Refresh();
    }

    protected override void OnDestroy()
    {
        DataSession.DataChanged -= OnDataChanged;
        base.OnDestroy();
    }

    // Data can be replaced from a background thread (loading) or a script.
    private void OnDataChanged() => RunOnUiThread(Refresh);

    protected override void OnResume()
    {
        base.OnResume();
        Refresh();
    }

    private void Refresh()
    {
        _info.Text = DataSession.Describe();
        Title = DataSession.Data is null ? "UndertaleModTool" : DataSession.DisplayName + (DataSession.IsModified ? " *" : "");
        _entries.Clear();

        UndertaleData data = DataSession.Data;
        if (data is not null)
        {
            if (data.GeneralInfo is not null)
                _entries.Add(("General info", () => data.GeneralInfo));
            if (data.Options is not null)
                _entries.Add(("Global init options", () => data.Options));
            if (data.Language is not null)
                _entries.Add(("Language", () => data.Language));

            foreach (var property in data.AllListProperties)
            {
                if (property.GetValue(data) is not IList list)
                    continue;
                string name = property.Name;
                _entries.Add(($"{name} ({list.Count})", () => property.GetValue(data)));
            }
        }

        _categories.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1,
                                                       _entries.Select(e => e.Name).ToList());
        InvalidateOptionsMenu();
    }

    #region Menu

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        bool loaded = DataSession.Data is not null;
        menu.Add(0, 1, 0, "Open")!.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, 2, 1, "Save")!.SetEnabled(loaded && DataSession.SourceUri is not null)!.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, 3, 2, "Save as...")!.SetEnabled(loaded);
        menu.Add(0, 4, 3, "Scripts")!.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, 5, 4, "Search in code...")!.SetEnabled(loaded);
        menu.Add(0, 6, 5, "Load warnings")!.SetEnabled(loaded && DataSession.LoadWarnings.Count > 0);
        menu.Add(0, 7, 6, "New data file");
        menu.Add(0, 8, 7, "Close file")!.SetEnabled(loaded);
        menu.Add(0, 9, 8, "Grant full storage access");
        menu.Add(0, 10, 9, "About");
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        switch (item.ItemId)
        {
            case 1:
                ConfirmDiscard(PickFileToOpen);
                return true;
            case 2:
                Save(DataSession.SourceUri);
                return true;
            case 3:
                PickSaveLocation();
                return true;
            case 4:
                StartActivity(new Intent(this, typeof(ScriptsActivity)));
                return true;
            case 5:
                StartActivity(new Intent(this, typeof(CodeSearchActivity)));
                return true;
            case 6:
                UiHelper.ShowLongText(this, "Load warnings", string.Join("\n\n", DataSession.LoadWarnings));
                return true;
            case 7:
                ConfirmDiscard(() => DataSession.SetData(UndertaleData.CreateNew(), null, null, "data.win (new)"));
                return true;
            case 8:
                ConfirmDiscard(DataSession.Close);
                return true;
            case 9:
                RequestFullStorageAccess();
                return true;
            case 10:
                UiHelper.ShowMessage(this, "About",
                    "UndertaleModTool for Android\n\n" +
                    "An Android port of UndertaleModTool by the Underminers team " +
                    "(https://github.com/UnderminersTeam/UndertaleModTool), built on the same UndertaleModLib " +
                    "and Underanalyzer decompiler/compiler.\n\n" +
                    $"Script working directory:\n{StorageHelper.Pretty(StorageHelper.WorkDirectory)}\n\n" +
                    "Licensed under GPLv3.");
                return true;
        }
        return base.OnOptionsItemSelected(item);
    }

    #endregion

    #region Open / save

    private void ConfirmDiscard(Action action)
    {
        if (DataSession.Data is not null && DataSession.IsModified)
            UiHelper.Confirm(this, "Unsaved changes", "The current data file has unsaved changes. Discard them?", action);
        else
            action();
    }

    private void PickFileToOpen()
    {
        Intent intent = new(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, RequestOpen);
    }

    private void PickSaveLocation()
    {
        Intent intent = new(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/octet-stream");
        intent.PutExtra(Intent.ExtraTitle, DataSession.DisplayName?.Replace(" (new)", "") ?? "data.win");
        StartActivityForResult(intent, RequestSaveAs);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null)
            return;

        global::Android.Net.Uri uri = data.Data;
        try
        {
            ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch
        {
            // Not all providers support persistable (or write) permissions.
        }

        if (requestCode == RequestOpen)
            Open(uri);
        else if (requestCode == RequestSaveAs)
            Save(uri);
    }

    private void Open(global::Android.Net.Uri uri)
    {
        UiHelper.RunWithProgress(this, "Loading", report => DataSession.LoadFromUri(this, uri, report), data =>
        {
            Refresh();
            if (data.IsYYC())
            {
                UiHelper.ShowMessage(this, "YYC",
                    "This game was compiled with the YoYo Compiler (YYC). The game code is compiled into the " +
                    "executable, so it cannot be viewed or edited here. Other resources can still be edited.");
            }
            else if (DataSession.LoadWarnings.Any(w => w.StartsWith("[!]")))
            {
                UiHelper.ShowLongText(this, "Warnings while loading", string.Join("\n\n", DataSession.LoadWarnings));
            }
        }, e =>
        {
            DataSession.Close();
            Refresh();
            UiHelper.ShowLongText(this, "Failed to load file",
                "An error occurred while trying to load the file. It may not be a GameMaker data file, " +
                "or it may use an unsupported format.\n\n" + e);
        });
    }

    private void Save(global::Android.Net.Uri uri)
    {
        if (uri is null)
        {
            PickSaveLocation();
            return;
        }
        UiHelper.RunWithProgress(this, "Saving", report =>
        {
            DataSession.SaveToUri(this, uri, report);
            return true;
        }, _ =>
        {
            Refresh();
            UiHelper.Toast(this, "Saved " + DataSession.DisplayName);
        }, e => UiHelper.ShowLongText(this, "Failed to save",
            "The file could not be saved. If this location is read-only, use \"Save as...\".\n\n" + e));
    }

    #endregion

    private void RequestFullStorageAccess()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (global::Android.OS.Environment.IsExternalStorageManager)
            {
                UiHelper.Toast(this, "Full storage access is already granted.");
                return;
            }
            Intent intent = new(Settings.ActionManageAppAllFilesAccessPermission,
                                global::Android.Net.Uri.Parse("package:" + PackageName));
            StartActivity(intent);
        }
        else
        {
            RequestPermissions(new[]
            {
                global::Android.Manifest.Permission.ReadExternalStorage,
                global::Android.Manifest.Permission.WriteExternalStorage,
            }, 100);
        }
    }

    public override void OnBackPressed()
    {
        if (DataSession.Data is not null && DataSession.IsModified)
        {
            UiHelper.Confirm(this, "Unsaved changes", "Exit without saving?", () => base.OnBackPressed());
            return;
        }
#pragma warning disable CA1422
        base.OnBackPressed();
#pragma warning restore CA1422
    }
}
