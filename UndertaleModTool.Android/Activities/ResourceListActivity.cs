using System.Collections;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Core.Assets;
using UndertaleModTool.Core.Imaging;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// Shows the items of a list (a resource category, or any list inside a resource) with a filter box.
/// </summary>
[Activity(Label = "Resources", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public class ResourceListActivity : BaseActivity
{
    private IEnumerable _source;
    private EditText _filter;
    private ListView _list;
    private ItemAdapter _adapter;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _source = DataSession.Fetch(Intent!.GetIntExtra(Navigator.ExtraHandle, 0)) as IEnumerable;
        Title = Intent.GetStringExtra(Navigator.ExtraTitle) ?? "Resources";
        if (_source is null)
        {
            Finish();
            return;
        }
        ActionBar?.SetDisplayHomeAsUpEnabled(true);

        LinearLayout root = UiHelper.VerticalLayout(this, 0);
        _filter = new EditText(this) { Hint = "Filter" };
        _filter.SetSingleLine(true);
        int p = this.Dp(8);
        _filter.SetPadding(this.Dp(16), p, this.Dp(16), p);
        _filter.TextChanged += (_, _) => ApplyFilter();
        root.AddView(_filter);

        _list = new ListView(this) { FastScrollEnabled = true };
        _list.ItemClick += (_, e) => OnItemClick(_adapter.IndexAt(e.Position));
        root.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        SetContentView(root);
        UiHelper.FitSystemWindows(root);

        _adapter = new ItemAdapter(this);
        _list.Adapter = _adapter;
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_source is not null)
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        string filter = _filter.Text?.Trim() ?? "";
        List<(int, string)> items = new();
        int index = 0;
        foreach (object item in _source)
        {
            string name = Navigator.DisplayName(item);
            if (filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                items.Add((index, name));
            index++;
        }
        _adapter.SetItems(items);
    }

    private object ItemAt(int index)
    {
        if (_source is IList list)
            return list[index];
        return _source.Cast<object>().ElementAt(index);
    }

    private void OnItemClick(int index)
    {
        object item = ItemAt(index);
        switch (item)
        {
            case UndertaleString str:
                // Strings are by far the most commonly edited thing, so edit them in place.
                UiHelper.PromptText(this, $"String #{index}", null, str.Content, true, value =>
                {
                    if (value is null || value == str.Content)
                        return;
                    str.Content = value;
                    DataSession.IsModified = true;
                    ApplyFilter();
                });
                break;
            case null:
                UiHelper.Toast(this, "(null)");
                break;
            default:
                if (item.GetType().IsPrimitive || item is string || item.GetType().IsEnum)
                    EditPrimitive(index, item);
                else
                    Navigator.Open(this, item);
                break;
        }
    }

    private void EditPrimitive(int index, object value)
    {
        if (_source is not IList { IsReadOnly: false } list)
        {
            UiHelper.ShowMessage(this, $"Item #{index}", value.ToString());
            return;
        }
        UiHelper.PromptText(this, $"Item #{index}", value.GetType().Name, value.ToString(), false, text =>
        {
            if (text is null)
                return;
            if (!ValueParser.TryParse(text, value.GetType(), out object parsed))
            {
                UiHelper.Toast(this, $"Invalid {value.GetType().Name}");
                return;
            }
            list[index] = parsed;
            DataSession.IsModified = true;
            ApplyFilter();
        });
    }

    #region Adding resources

    private bool IsSounds => DataSession.Data is not null && ReferenceEquals(_source, DataSession.Data.Sounds);
    private bool IsSprites => DataSession.Data is not null && ReferenceEquals(_source, DataSession.Data.Sprites);

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        if (IsSounds)
            menu.Add(0, 1, 0, "Add sound...");
        if (IsSprites)
        {
            menu.Add(0, 2, 0, "New sprite from image...");
            menu.Add(0, 3, 1, "Import images from folder...");
        }
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        switch (item.ItemId)
        {
            case global::Android.Resource.Id.Home:
                Finish();
                return true;
            case 1:
                PickAndName("audio/*", "snd_new", "Sound name", (bytes, name) =>
                    AssetTools.AddSound(DataSession.Data, name, bytes).Name.Content);
                return true;
            case 2:
                PickAndName("image/*", "spr_new", "Sprite name", (bytes, name) =>
                {
                    if (DataSession.Data.Sprites.ByName(name) is not null)
                        throw new InvalidOperationException($"A sprite named \"{name}\" already exists.");
                    ArgbImage image = DdsDecoder.IsDds(bytes) ? DdsDecoder.Decode(bytes) : ImageHelper.DecodeExact(bytes);
                    return AssetTools.ImportImages(DataSession.Data, new[] { (name + "_0", image) }).ToString();
                });
                return true;
            case 3:
                FileBrowserDialog.Show(this, Services.FileBrowserMode.Directory, null, folder =>
                {
                    if (folder is null)
                        return;
                    UiHelper.RunWithProgress(this, "Importing images", report =>
                    {
                        var files = Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
                        List<(string, ArgbImage)> images = new();
                        foreach (string file in files)
                        {
                            report($"Reading {Path.GetFileName(file)}");
                            images.Add((Path.GetFileNameWithoutExtension(file), PngDecoder.Decode(file)));
                        }
                        report("Packing texture pages...");
                        return AssetTools.ImportImages(DataSession.Data, images).ToString();
                    }, summary =>
                    {
                        DataSession.IsModified = true;
                        ApplyFilter();
                        UiHelper.ShowLongText(this, "Import finished", summary);
                    });
                });
                return true;
        }
        return base.OnOptionsItemSelected(item);
    }

    /// <summary>Picks a file, asks for a name, then runs <paramref name="create"/> in the background.</summary>
    private void PickAndName(string mime, string defaultName, string label, Func<byte[], string, string> create)
    {
        Intent intent = new(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mime);
        StartForResult(intent, (result, data) =>
        {
            if (result != Result.Ok || data?.Data is null)
                return;
            UiHelper.PromptText(this, label, null, defaultName, false, name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;
                UiHelper.RunWithProgress(this, "Adding", _ =>
                {
                    using Stream input = ContentResolver!.OpenInputStream(data.Data)!;
                    using MemoryStream ms = new();
                    input.CopyTo(ms);
                    return create(ms.ToArray(), name.Trim());
                }, message =>
                {
                    DataSession.IsModified = true;
                    ApplyFilter();
                    UiHelper.Toast(this, "Added " + message);
                });
            });
        });
    }

    #endregion

    private sealed class ItemAdapter : BaseAdapter
    {
        private readonly Activity _context;
        private List<(int Index, string Name)> _items = new();

        public ItemAdapter(Activity context) => _context = context;

        public void SetItems(List<(int, string)> items)
        {
            _items = items;
            NotifyDataSetChanged();
        }

        public int IndexAt(int position) => _items[position].Index;

        public override int Count => _items.Count;

        public override Java.Lang.Object GetItem(int position) => null;

        public override long GetItemId(int position) => _items[position].Index;

        public override View GetView(int position, View convertView, ViewGroup parent)
        {
            TextView view = convertView as TextView ?? CreateView();
            var (index, name) = _items[position];
            string shown = name.Length > 300 ? name[..300] + "..." : name;
            view.Text = $"{index}: {shown.Replace('\n', ' ')}";
            return view;
        }

        private TextView CreateView()
        {
            TextView view = new(_context);
            int p = _context.Dp(12);
            view.SetPadding(_context.Dp(16), p, _context.Dp(16), p);
            view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
            view.SetMaxLines(3);
            view.Ellipsize = TextUtils.TruncateAt.End;
            return view;
        }
    }
}
