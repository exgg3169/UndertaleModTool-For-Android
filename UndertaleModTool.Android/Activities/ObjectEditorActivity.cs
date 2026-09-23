using System.Collections;
using System.Reflection;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
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
public class ObjectEditorActivity : BaseActivity
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

    #region Menu

    private const int MenuExportPng = 1, MenuReplaceImage = 2, MenuPlay = 3, MenuStop = 4, MenuReplaceAudio = 5,
                      MenuExportAudio = 6, MenuOpenAudioGroup = 7, MenuRoomEditor = 8;

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        if (_target is UndertaleRoom)
            menu.Add(0, MenuRoomEditor, 0, "Room editor")!.SetShowAsAction(ShowAsAction.Always);
        if (ImageHelper.HasPreview(_target))
            menu.Add(0, MenuReplaceImage, 1, "Replace image...");
        if (_preview is not null)
            menu.Add(0, MenuExportPng, 2, "Export PNG");
        if (_target is UndertaleSound or UndertaleEmbeddedAudio)
        {
            menu.Add(0, MenuPlay, 3, "Play")!.SetShowAsAction(ShowAsAction.Always);
            menu.Add(0, MenuStop, 4, "Stop");
            menu.Add(0, MenuReplaceAudio, 5, "Replace audio...");
            menu.Add(0, MenuExportAudio, 6, "Export audio");
        }
        if (_target is UndertaleSound sound && !AudioHelper.IsBuiltinGroup(sound))
            menu.Add(0, MenuOpenAudioGroup, 7, $"Open {AudioHelper.GroupFileName(sound.GroupID)}...");
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        switch (item.ItemId)
        {
            case global::Android.Resource.Id.Home:
                Finish();
                return true;
            case MenuExportPng when _preview is not null:
                ExportPng();
                return true;
            case MenuReplaceImage:
                ReplaceImage();
                return true;
            case MenuPlay:
                PlayAudio();
                return true;
            case MenuStop:
                AudioHelper.Stop();
                return true;
            case MenuReplaceAudio:
                ReplaceAudio();
                return true;
            case MenuExportAudio:
                ExportAudio();
                return true;
            case MenuOpenAudioGroup when _target is UndertaleSound sound:
                OpenAudioGroup(sound.GroupID);
                return true;
            case MenuRoomEditor when _target is UndertaleRoom room:
                Intent intent = new(this, typeof(RoomEditorActivity));
                intent.PutExtra(Navigator.ExtraHandle, DataSession.Park(room));
                StartActivity(intent);
                return true;
        }
        return base.OnOptionsItemSelected(item);
    }

    protected override void OnPause()
    {
        base.OnPause();
        AudioHelper.Stop();
    }

    private string SafeFileName()
    {
        string name = Navigator.DisplayName(_target);
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private void ExportPng()
    {
        string dir = System.IO.Path.Combine(StorageHelper.WorkDirectory, "Exported images");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, SafeFileName() + ".png");
        using (FileStream fs = File.Create(path))
            _preview.Compress(Bitmap.CompressFormat.Png!, 100, fs);
        UiHelper.ShowMessage(this, "Exported", "Saved to " + StorageHelper.Pretty(path));
    }

    #endregion

    #region Image import

    private Intent PickIntent(string mime)
    {
        Intent intent = new(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mime);
        return intent;
    }

    private void ReplaceImage()
    {
        switch (_target)
        {
            case UndertaleSprite { Textures.Count: > 1 } sprite:
                string[] frames = Enumerable.Range(0, sprite.Textures.Count).Select(i => $"Frame {i}").ToArray();
                new AlertDialog.Builder(this)
                    .SetTitle("Which frame?")!
                    .SetItems(frames, (_, e) => PickImageFor(sprite.Textures[e.Which]?.Texture, null))!
                    .Show();
                break;
            case UndertaleSprite sprite:
                PickImageFor(sprite.Textures.FirstOrDefault()?.Texture, null);
                break;
            case UndertaleSprite.TextureEntry entry:
                PickImageFor(entry.Texture, null);
                break;
            case UndertaleBackground background:
                PickImageFor(background.Texture, null);
                break;
            case UndertaleFont font:
                PickImageFor(font.Texture, null);
                break;
            case UndertaleTexturePageItem pageItem:
                PickImageFor(pageItem, null);
                break;
            case UndertaleEmbeddedTexture texture:
                PickImageFor(null, texture);
                break;
        }
    }

    /// <summary>Picks an image and puts it into a texture page item, or replaces a whole page.</summary>
    private void PickImageFor(UndertaleTexturePageItem item, UndertaleEmbeddedTexture page)
    {
        if (item is null && page is null)
        {
            UiHelper.ShowMessage(this, "Replace image", "There is no texture to replace.");
            return;
        }

        StartForResult(PickIntent("image/*"), (result, data) =>
        {
            if (result != Result.Ok || data?.Data is null)
                return;
            ArgbImage image;
            try
            {
                image = ImageHelper.DecodeExact(this, data.Data);
            }
            catch (Exception e)
            {
                UiHelper.ShowLongText(this, "Can't read image", e.ToString());
                return;
            }

            string warning = null;
            if (item is not null && (image.Width != item.SourceWidth || image.Height != item.SourceHeight))
            {
                warning = $"The image is {image.Width}x{image.Height}, but the space for it on the texture page is " +
                          $"{item.SourceWidth}x{item.SourceHeight}. It will be scaled to fit (like the desktop tool does).";
            }
            else if (page is not null)
            {
                GMImage current = page.TextureData?.Image;
                if (current is not null && (current.Width != image.Width || current.Height != image.Height))
                {
                    warning = $"The image is {image.Width}x{image.Height}, but the current page is {current.Width}x{current.Height}. " +
                              "Texture page items on this page keep their coordinates, so sprites may break.";
                }
            }

            void Apply() => UiHelper.RunWithProgress(this, "Replacing image", _ =>
            {
                if (page is not null)
                    ImageCodec.ReplacePage(page, image);
                else
                    ImageCodec.ReplacePageItem(item, image, ImageHelper.DecodePageExact);
                return true;
            }, _ =>
            {
                DataSession.IsModified = true;
                UiHelper.Toast(this, "Image replaced");
                _preview = null;
                Build();
                InvalidateOptionsMenu();
            });

            if (warning is null)
                Apply();
            else
                UiHelper.Confirm(this, "Replace image", warning, Apply, yes: "Continue", no: "Cancel");
        });
    }

    #endregion

    #region Audio

    private UndertaleEmbeddedAudio TargetAudio(out string reason)
    {
        reason = null;
        return _target switch
        {
            UndertaleEmbeddedAudio audio => audio,
            UndertaleSound sound => AudioHelper.GetEmbeddedAudio(sound, out reason),
            _ => null,
        };
    }

    private void ShowAudioProblem(string reason)
    {
        if (_target is UndertaleSound sound && !AudioHelper.IsBuiltinGroup(sound) && AudioHelper.GetGroupData(sound) is null)
        {
            UiHelper.Confirm(this, "Audio group", reason, () => OpenAudioGroup(sound.GroupID), yes: "Open file", no: "Cancel");
            return;
        }
        UiHelper.ShowMessage(this, "Audio", reason ?? "No audio data.");
    }

    private void PlayAudio()
    {
        UndertaleEmbeddedAudio audio = TargetAudio(out string reason);
        if (audio?.Data is not { Length: > 0 })
        {
            ShowAudioProblem(reason);
            return;
        }
        try
        {
            AudioHelper.Play(this, audio.Data);
        }
        catch (Exception e)
        {
            UiHelper.ShowLongText(this, "Playback failed", e.ToString());
        }
    }

    private void ExportAudio()
    {
        UndertaleEmbeddedAudio audio = TargetAudio(out string reason);
        if (audio?.Data is not { Length: > 0 })
        {
            ShowAudioProblem(reason);
            return;
        }
        string dir = System.IO.Path.Combine(StorageHelper.WorkDirectory, "Exported sounds");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, SafeFileName() + AudioHelper.DetectExtension(audio.Data));
        File.WriteAllBytes(path, audio.Data);
        UiHelper.ShowMessage(this, "Exported", "Saved to " + StorageHelper.Pretty(path));
    }

    private void ReplaceAudio()
    {
        if (TargetAudio(out string reason) is null)
        {
            ShowAudioProblem(reason);
            return;
        }
        StartForResult(PickIntent("audio/*"), (result, data) =>
        {
            if (result != Result.Ok || data?.Data is null)
                return;
            AudioHelper.Stop();
            UiHelper.RunWithProgress(this, "Replacing audio", _ =>
            {
                byte[] bytes;
                using (Stream input = ContentResolver!.OpenInputStream(data.Data)!)
                using (MemoryStream ms = new())
                {
                    input.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                if (_target is UndertaleSound sound)
                {
                    AudioHelper.ReplaceSoundAudio(this, sound, bytes);
                }
                else if (_target is UndertaleEmbeddedAudio audio)
                {
                    if (AudioHelper.DetectExtension(bytes) is not (".wav" or ".ogg"))
                        throw new InvalidDataException("Only WAV and OGG files can be used as GameMaker sounds.");
                    audio.Data = bytes;
                    DataSession.IsModified = true;
                }
                return true;
            }, _ =>
            {
                UiHelper.Toast(this, "Audio replaced");
                Build();
            });
        });
    }

    private void OpenAudioGroup(int groupId)
    {
        Intent intent = PickIntent("*/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartForResult(intent, (result, data) =>
        {
            if (result != Result.Ok || data?.Data is null)
                return;
            try
            {
                ContentResolver!.TakePersistableUriPermission(data.Data, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            }
            catch
            {
                // Read-only is fine for playback.
            }
            UiHelper.RunWithProgress(this, "Loading audio group", _ => AudioHelper.LoadGroupFile(this, data.Data, groupId),
                count => UiHelper.Toast(this, $"Loaded {count} sounds from {AudioHelper.GroupFileName(groupId)}"));
        });
    }

    #endregion
}
