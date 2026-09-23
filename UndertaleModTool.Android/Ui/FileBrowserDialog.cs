using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using UndertaleModTool.Android.Activities;
using UndertaleModTool.Android.Services;

namespace UndertaleModTool.Android.Services
{
    public enum FileBrowserMode
    {
        Directory,
        OpenFile,
        SaveFile,
    }
}

namespace UndertaleModTool.Android.Ui
{
    /// <summary>
    /// A simple file system browser, used where desktop scripts expect a file/folder dialog
    /// returning a real path. Documents from other apps can be imported into the working
    /// directory through the system picker.
    /// </summary>
    public sealed class FileBrowserDialog
    {
        private const string SharedStorageRoot = "/storage/emulated/0";

        private readonly BaseActivity _activity;
        private readonly FileBrowserMode _mode;
        private readonly string _defaultExt;
        private readonly Action<string> _onResult;
        private string _current;
        private AlertDialog _dialog;
        private TextView _pathLabel;
        private EditText _fileName;
        private ArrayAdapter<string> _adapter;
        private readonly List<(string Label, string Path)> _entries = new();
        private bool _finished;

        private FileBrowserDialog(BaseActivity activity, FileBrowserMode mode, string defaultExt, Action<string> onResult)
        {
            _activity = activity;
            _mode = mode;
            _defaultExt = defaultExt?.TrimStart('.');
            _onResult = onResult;
            _current = StorageHelper.WorkDirectory;
        }

        public static void Show(BaseActivity activity, FileBrowserMode mode, string defaultExt, Action<string> onResult)
            => new FileBrowserDialog(activity, mode, defaultExt, onResult).Show();

        private void Show()
        {
            LinearLayout layout = UiHelper.VerticalLayout(_activity, 12);
            _pathLabel = UiHelper.Label(_activity, "", 12);
            layout.AddView(_pathLabel);

            if (_mode == FileBrowserMode.SaveFile)
            {
                _fileName = new EditText(_activity) { Hint = "File name", Text = _defaultExt is null ? "" : "output." + _defaultExt };
                _fileName.SetSingleLine(true);
                layout.AddView(_fileName);
            }

            ListView list = new(_activity);
            _adapter = new ArrayAdapter<string>(_activity, global::Android.Resource.Layout.SimpleListItem1, new List<string>());
            list.Adapter = _adapter;
            list.ItemClick += (_, e) => OnEntryClick(_entries[e.Position].Path);
            layout.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, _activity.Dp(360)));

            string title = _mode switch
            {
                FileBrowserMode.Directory => "Choose a folder",
                FileBrowserMode.OpenFile => "Choose a file",
                _ => "Save file",
            };
            AlertDialog.Builder builder = new AlertDialog.Builder(_activity)
                .SetTitle(title)!
                .SetView(layout)!
                .SetNegativeButton("Cancel", (_, _) => Finish(null))!
                .SetOnCancelListener(new CancelListener(() => Finish(null)))!;

            if (_mode != FileBrowserMode.OpenFile)
                builder.SetPositiveButton(_mode == FileBrowserMode.Directory ? "Use this folder" : "Save", (EventHandler<DialogClickEventArgs>)null);
            builder.SetNeutralButton(_mode == FileBrowserMode.OpenFile ? "From device..." : "New folder", (EventHandler<DialogClickEventArgs>)null);

            _dialog = builder.Create()!;
            _dialog.Show();

            // Override the buttons so they don't dismiss the dialog when validation fails.
            _dialog.GetButton((int)DialogButtonType.Positive)?.SetOnClickListener(new ClickListener(OnPositive));
            _dialog.GetButton((int)DialogButtonType.Neutral)!.SetOnClickListener(new ClickListener(OnNeutral));

            Navigate(_current);
        }

        private void Navigate(string directory)
        {
            DirectoryInfo dir;
            try
            {
                dir = new DirectoryInfo(directory);
                // Enumerate first so that inaccessible directories fail here.
                var subdirs = dir.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var files = _mode == FileBrowserMode.Directory
                    ? new List<FileInfo>()
                    : dir.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

                _current = dir.FullName;
                _entries.Clear();
                _entries.Add(("[Script working folder]", StorageHelper.WorkDirectory));
                if (Directory.Exists(SharedStorageRoot) && CanList(SharedStorageRoot))
                    _entries.Add(("[Internal storage]", SharedStorageRoot));
                if (dir.Parent is not null && CanList(dir.Parent.FullName))
                    _entries.Add(("..", dir.Parent.FullName));
                _entries.AddRange(subdirs.Select(d => ("📁 " + d.Name, d.FullName)));
                _entries.AddRange(files.Select(f => ("📄 " + f.Name, f.FullName)));
            }
            catch (Exception e)
            {
                UiHelper.Toast(_activity, "Cannot open folder: " + e.Message);
                return;
            }

            _pathLabel.Text = StorageHelper.Pretty(_current);
            _adapter.Clear();
            _adapter.AddAll(_entries.Select(e => e.Label).ToList());
            _adapter.NotifyDataSetChanged();
        }

        private static bool CanList(string path)
        {
            try
            {
                Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnEntryClick(string path)
        {
            if (Directory.Exists(path))
            {
                Navigate(path);
                return;
            }
            if (_mode == FileBrowserMode.OpenFile)
                Finish(path);
            else if (_mode == FileBrowserMode.SaveFile)
                _fileName.Text = Path.GetFileName(path);
        }

        private void OnPositive()
        {
            if (_mode == FileBrowserMode.Directory)
            {
                Finish(_current);
                return;
            }

            string name = _fileName.Text?.Trim();
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _fileName.Error = "Enter a valid file name";
                return;
            }
            if (_defaultExt is not null && !Path.HasExtension(name))
                name += "." + _defaultExt;
            Finish(Path.Combine(_current, name));
        }

        private void OnNeutral()
        {
            if (_mode == FileBrowserMode.OpenFile)
            {
                ImportFromDevice();
                return;
            }

            UiHelper.PromptText(_activity, "New folder", null, "", false, name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;
                try
                {
                    string path = Path.Combine(_current, name.Trim());
                    Directory.CreateDirectory(path);
                    Navigate(path);
                }
                catch (Exception e)
                {
                    UiHelper.Toast(_activity, e.Message);
                }
            });
        }

        private void ImportFromDevice()
        {
            Intent intent = new(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            _activity.StartForResult(intent, (result, data) =>
            {
                if (result != Result.Ok || data?.Data is null)
                    return;
                try
                {
                    string imported = Path.Combine(StorageHelper.WorkDirectory, "Imported");
                    Finish(StorageHelper.CopyUriToDirectory(_activity, data.Data, imported));
                }
                catch (Exception e)
                {
                    UiHelper.ShowLongText(_activity, "Import failed", e.ToString());
                }
            });
        }

        private void Finish(string path)
        {
            if (_finished)
                return;
            _finished = true;
            try
            {
                _dialog?.Dismiss();
            }
            catch
            {
                // Ignore
            }
            _onResult(path);
        }

        private sealed class ClickListener : Java.Lang.Object, View.IOnClickListener
        {
            private readonly Action _action;
            public ClickListener(Action action) => _action = action;
            public void OnClick(View v) => _action();
        }

        private sealed class CancelListener : Java.Lang.Object, IDialogInterfaceOnCancelListener
        {
            private readonly Action _action;
            public CancelListener(Action action) => _action = action;
            public void OnCancel(IDialogInterface dialog) => _action();
        }
    }
}
