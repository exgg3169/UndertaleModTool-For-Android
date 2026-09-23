using System.Collections;
using System.Reflection;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// Generic, reflection-based editor for any UndertaleModLib object.
/// </summary>
/// <remarks>
/// The desktop tool has a hand-written WPF editor per resource type; here, every public
/// property is shown: primitives, enums and strings are edited in place, and references
/// and lists open another screen.
/// </remarks>
[Activity(Label = "Editor", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout,
          WindowSoftInputMode = SoftInput.AdjustResize)]
public class ObjectEditorActivity : Activity
{
    private object _target;
    private ScrollView _scroll;
    private Bitmap _preview;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _target = DataSession.Fetch(Intent!.GetIntExtra(Navigator.ExtraHandle, 0));
        Title = Intent.GetStringExtra(Navigator.ExtraTitle) ?? "Editor";
        if (_target is null)
        {
            Finish();
            return;
        }
        ActionBar?.SetDisplayHomeAsUpEnabled(true);
        ActionBar!.Subtitle = Navigator.FriendlyTypeName(_target.GetType());

        _scroll = new ScrollView(this);
        SetContentView(_scroll);
        UiHelper.FitSystemWindows(_scroll);
        Build();
    }

    protected override void OnRestart()
    {
        base.OnRestart();
        int y = _scroll.ScrollY;
        Build();
        _scroll.Post(() => _scroll.ScrollTo(0, y));
    }

    private void Build()
    {
        _scroll.RemoveAllViews();
        LinearLayout layout = UiHelper.VerticalLayout(this, 16);

        if (ImageHelper.HasPreview(_target))
            AddPreview(layout);

        if (_target is UndertaleString str)
        {
            AddStringContentEditor(layout, str);
        }
        else
        {
            Type type = _target.GetType();
            foreach (MemberInfo member in GetMembers(type))
                AddMemberRow(layout, member);
        }

        _scroll.AddView(layout);
    }

    private static IEnumerable<MemberInfo> GetMembers(Type type)
    {
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true } &&
                p.GetCustomAttribute<ObsoleteAttribute>() is null)
            {
                yield return p;
            }
        }
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            yield return f;
    }

    #region Rows

    private void AddMemberRow(LinearLayout layout, MemberInfo member)
    {
        Type type;
        object value;
        bool canWrite;
        Action<object> setter;
        try
        {
            switch (member)
            {
                case PropertyInfo p:
                    type = p.PropertyType;
                    value = p.GetValue(_target);
                    canWrite = p.SetMethod is { IsPublic: true };
                    setter = v => p.SetValue(_target, v);
                    break;
                case FieldInfo f:
                    type = f.FieldType;
                    value = f.GetValue(_target);
                    canWrite = !f.IsInitOnly && !_target.GetType().IsValueType;
                    setter = v => f.SetValue(_target, v);
                    break;
                default:
                    return;
            }
        }
        catch (Exception e)
        {
            layout.AddView(UiHelper.Header(this, member.Name));
            layout.AddView(UiHelper.Label(this, $"(error: {(e.InnerException ?? e).Message})"));
            return;
        }

        layout.AddView(UiHelper.Header(this, member.Name));

        void Set(object v)
        {
            setter(v);
            DataSession.IsModified = true;
        }

        if (type == typeof(UndertaleString))
        {
            AddUndertaleStringRow(layout, (UndertaleString)value, canWrite ? Set : null);
        }
        else if (type == typeof(bool))
        {
            CheckBox check = new(this) { Checked = (bool)value, Enabled = canWrite, Text = member.Name };
            check.CheckedChange += (_, e) => Set(e.IsChecked);
            layout.AddView(check);
        }
        else if (type.IsEnum && type.GetCustomAttribute<FlagsAttribute>() is null)
        {
            AddEnumRow(layout, type, value, canWrite ? Set : null);
        }
        else if (ValueParser.IsEditable(type))
        {
            AddTextRow(layout, type, value, canWrite ? Set : null);
        }
        else if (value is null)
        {
            layout.AddView(UiHelper.Label(this, "(null)"));
        }
        else if (value is IEnumerable enumerable and not string)
        {
            int count = value is ICollection c ? c.Count : enumerable.Cast<object>().Count();
            layout.AddView(UiHelper.Button(this, $"{Navigator.FriendlyTypeName(type)} - {count} item(s)",
                () => Navigator.Open(this, value, $"{Title}.{member.Name}")));
        }
        else if (type.IsValueType)
        {
            layout.AddView(UiHelper.Label(this, ValueParser.Format(value)));
        }
        else
        {
            object resolved = Navigator.Unwrap(value);
            string text = resolved is null ? "(null)" : $"{Navigator.DisplayName(resolved)}  ›";
            Button button = UiHelper.Button(this, text, () => Navigator.Open(this, resolved, member.Name));
            button.Enabled = resolved is not null;
            layout.AddView(button);
        }
    }

    private void AddTextRow(LinearLayout layout, Type type, object value, Action<object> set)
    {
        EditText edit = new(this) { Text = ValueParser.Format(value), Enabled = set is not null };
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying != typeof(string) && !underlying.IsEnum)
        {
            edit.SetSingleLine(true);
            edit.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        }
        edit.TextChanged += (_, _) =>
        {
            if (set is null)
                return;
            if (ValueParser.TryParse(edit.Text, type, out object parsed))
            {
                edit.Error = null;
                if (!Equals(parsed, value))
                {
                    set(parsed);
                    value = parsed;
                }
            }
            else
            {
                edit.Error = $"Not a valid {underlying.Name}";
            }
        };
        layout.AddView(edit);
    }

    private void AddEnumRow(LinearLayout layout, Type type, object value, Action<object> set)
    {
        List<object> values = Enum.GetValues(type).Cast<object>().ToList();
        List<string> names = values.Select(v => v.ToString()).ToList();
        int selected = values.IndexOf(value);
        if (selected < 0)
        {
            values.Add(value);
            names.Add(ValueParser.Format(value) + " (unknown)");
            selected = values.Count - 1;
        }

        Spinner spinner = new(this) { Enabled = set is not null };
        spinner.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, names);
        spinner.SetSelection(selected);
        spinner.ItemSelected += (_, e) =>
        {
            if (set is null || e.Position == selected)
                return;
            selected = e.Position;
            set(values[e.Position]);
        };
        layout.AddView(spinner);
    }

    private void AddUndertaleStringRow(LinearLayout layout, UndertaleString str, Action<object> set)
    {
        EditText edit = new(this) { Text = str?.Content ?? "", Hint = str is null ? "(null)" : null };
        edit.Enabled = str is not null || set is not null;
        edit.TextChanged += (_, _) =>
        {
            if (str is null)
            {
                if (set is null || DataSession.Data is null)
                    return;
                str = DataSession.Data.Strings.MakeString(edit.Text);
                set(str);
                return;
            }
            if (str.Content != edit.Text)
            {
                // Like the desktop tool, this edits the shared string entry in place.
                str.Content = edit.Text;
                DataSession.IsModified = true;
            }
        };
        layout.AddView(edit);
    }

    private void AddStringContentEditor(LinearLayout layout, UndertaleString str)
    {
        layout.AddView(UiHelper.Header(this, "Content"));
        EditText edit = new(this) { Text = str.Content ?? "" };
        edit.InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine;
        edit.TextChanged += (_, _) =>
        {
            if (str.Content == edit.Text)
                return;
            str.Content = edit.Text;
            DataSession.IsModified = true;
        };
        layout.AddView(edit);
    }

    private void AddPreview(LinearLayout layout)
    {
        ImageView image = new(this);
        image.SetAdjustViewBounds(true);
        image.SetScaleType(ImageView.ScaleType.FitCenter);
        image.SetMaxHeight(this.Dp(320));
        image.SetBackgroundColor(Color.ParseColor("#FF404040"));
        TextView status = UiHelper.Label(this, "Loading preview...", 12);
        layout.AddView(image, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        layout.AddView(status);

        object target = _target;
        Task.Run(() =>
        {
            Bitmap bitmap = null;
            string error = null;
            try
            {
                bitmap = ImageHelper.GetPreview(target);
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            RunOnUiThread(() =>
            {
                _preview = bitmap;
                if (bitmap is null)
                {
                    status.Text = error is null ? "(no preview available for this image format)" : "Preview failed: " + error;
                    return;
                }
                // Pixel art: scale up without smoothing.
                var drawable = new global::Android.Graphics.Drawables.BitmapDrawable(Resources, bitmap);
                drawable.Paint!.FilterBitmap = false;
                image.SetImageDrawable(drawable);
                image.SetMinimumHeight(Math.Min(this.Dp(320), this.Dp(Math.Max(64, bitmap.Height))));
                status.Text = $"{bitmap.Width} x {bitmap.Height}";
                InvalidateOptionsMenu();
            });
        });
    }

    #endregion

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        if (_preview is not null)
            menu.Add(0, 1, 0, "Export PNG");
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        if (item.ItemId == global::Android.Resource.Id.Home)
        {
            Finish();
            return true;
        }
        if (item.ItemId == 1 && _preview is not null)
        {
            string dir = System.IO.Path.Combine(StorageHelper.WorkDirectory, "Exported images");
            Directory.CreateDirectory(dir);
            string name = Navigator.DisplayName(_target);
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            string path = System.IO.Path.Combine(dir, name + ".png");
            using (FileStream fs = File.Create(path))
                _preview.Compress(Bitmap.CompressFormat.Png!, 100, fs);
            UiHelper.ShowMessage(this, "Exported", "Saved to " + StorageHelper.Pretty(path));
            return true;
        }
        return base.OnOptionsItemSelected(item);
    }
}
