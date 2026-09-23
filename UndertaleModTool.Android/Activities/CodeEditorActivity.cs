using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using UndertaleModLib.Models;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// GML (decompiled) and bytecode assembly editor for a code entry.
/// </summary>
[Activity(Label = "Code", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden,
          WindowSoftInputMode = SoftInput.AdjustResize)]
public class CodeEditorActivity : Activity
{
    public const string ExtraLine = "line";

    private UndertaleCode _code;
    private EditText _editor;
    private Button _gmlTab, _asmTab;
    private bool _assemblyMode;
    private string _loadedText;
    private string _lastFind = "";
    private int _goToLine;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _code = Navigator.Unwrap(DataSession.Fetch(Intent!.GetIntExtra(Navigator.ExtraHandle, 0))) as UndertaleCode;
        if (_code is null || DataSession.Data is null)
        {
            Finish();
            return;
        }
        _goToLine = Intent.GetIntExtra(ExtraLine, 0);
        Title = _code.Name?.Content ?? "Code";
        ActionBar?.SetDisplayHomeAsUpEnabled(true);

        LinearLayout root = UiHelper.VerticalLayout(this, 0);

        LinearLayout tabs = new(this) { Orientation = Orientation.Horizontal };
        _gmlTab = UiHelper.Button(this, "Decompiled (GML)", () => SwitchMode(false));
        _asmTab = UiHelper.Button(this, "Disassembly", () => SwitchMode(true));
        tabs.AddView(_gmlTab, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        tabs.AddView(_asmTab, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        root.AddView(tabs);

        _editor = UiHelper.MonospaceEditor(this, "");
        int p = this.Dp(8);
        _editor.SetPadding(p, p, p, p);
        ScrollView scroll = new(this) { FillViewport = true };
        scroll.AddView(_editor);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        SetContentView(root);
        UiHelper.FitSystemWindows(root);
        LoadText();
    }

    private bool IsDirty => _loadedText is not null && _editor.Text != _loadedText;

    private void SwitchMode(bool assembly)
    {
        if (assembly == _assemblyMode)
            return;
        void Go()
        {
            _assemblyMode = assembly;
            LoadText();
        }
        if (IsDirty)
            UiHelper.Confirm(this, "Unsaved changes", "Discard changes to the current view?", Go);
        else
            Go();
    }

    private void LoadText()
    {
        _gmlTab.Enabled = _assemblyMode;
        _asmTab.Enabled = !_assemblyMode;
        _loadedText = null;
        _editor.Text = _assemblyMode ? "Disassembling..." : "Decompiling...";
        _editor.Enabled = false;

        UndertaleCode code = _code;
        bool assembly = _assemblyMode;
        Task.Run(() =>
        {
            string text = assembly
                ? CodeHelper.Disassemble(DataSession.Data, code)
                : CodeHelper.Decompile(DataSession.Data, code);
            RunOnUiThread(() =>
            {
                if (assembly != _assemblyMode)
                    return;
                _editor.Text = text;
                _loadedText = _editor.Text;
                _editor.Enabled = true;
                if (!assembly && _goToLine > 0)
                {
                    GoToLine(_goToLine);
                    _goToLine = 0;
                }
            });
        });
    }

    private void GoToLine(int line)
    {
        string text = _editor.Text ?? "";
        int index = 0;
        for (int i = 1; i < line && index >= 0; i++)
        {
            index = text.IndexOf('\n', index);
            if (index >= 0)
                index++;
        }
        if (index < 0)
            return;
        int end = text.IndexOf('\n', index);
        _editor.RequestFocus();
        _editor.SetSelection(index, end < 0 ? text.Length : end);
    }

    private void Save()
    {
        if (DataSession.Data is null)
            return;
        string source = _editor.Text ?? "";
        bool assembly = _assemblyMode;
        UiHelper.RunWithProgress(this, assembly ? "Assembling" : "Compiling", _ =>
        {
            Action<Action> onMainThread = action =>
            {
                using ManualResetEventSlim done = new();
                Exception error = null;
                RunOnUiThread(() =>
                {
                    try { action(); }
                    catch (Exception e) { error = e; }
                    finally { done.Set(); }
                });
                done.Wait();
                if (error is not null)
                    throw error;
            };
            return assembly
                ? CodeHelper.Assemble(DataSession.Data, _code, source, onMainThread)
                : CodeHelper.Compile(DataSession.Data, _code, source, onMainThread);
        }, error =>
        {
            if (error is not null)
            {
                UiHelper.ShowLongText(this, assembly ? "Assembler error" : "Compiler error", error);
                return;
            }
            UiHelper.Toast(this, assembly ? "Assembled" : "Compiled");
            LoadText();
        });
    }

    private void Find()
    {
        UiHelper.PromptText(this, "Find", null, _lastFind, false, query =>
        {
            if (string.IsNullOrEmpty(query))
                return;
            _lastFind = query;
            FindNext();
        });
    }

    private void FindNext()
    {
        if (string.IsNullOrEmpty(_lastFind))
        {
            Find();
            return;
        }
        string text = _editor.Text ?? "";
        int start = Math.Min(_editor.SelectionEnd, text.Length);
        int index = text.IndexOf(_lastFind, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            index = text.IndexOf(_lastFind, 0, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            UiHelper.Toast(this, "Not found");
            return;
        }
        _editor.RequestFocus();
        _editor.SetSelection(index, index + _lastFind.Length);
    }

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        menu.Add(0, 1, 0, "Compile")!.SetShowAsAction(ShowAsAction.Always);
        menu.Add(0, 2, 1, "Find")!.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, 3, 2, "Find next");
        menu.Add(0, 4, 3, "Go to line...");
        menu.Add(0, 5, 4, "Revert changes");
        menu.Add(0, 6, 5, "Code entry properties");
        return true;
    }

    public override bool OnPrepareOptionsMenu(IMenu menu)
    {
        menu.FindItem(1)?.SetTitle(_assemblyMode ? "Assemble" : "Compile");
        return base.OnPrepareOptionsMenu(menu);
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        switch (item.ItemId)
        {
            case global::Android.Resource.Id.Home:
                OnBackPressed();
                return true;
            case 1:
                Save();
                return true;
            case 2:
                Find();
                return true;
            case 3:
                FindNext();
                return true;
            case 4:
                UiHelper.PromptText(this, "Go to line", null, "", false, text =>
                {
                    if (int.TryParse(text, out int line))
                        GoToLine(line);
                });
                return true;
            case 5:
                LoadText();
                return true;
            case 6:
                Navigator.Open(this, new CodeEntryView(_code), _code.Name?.Content);
                return true;
        }
        return base.OnOptionsItemSelected(item);
    }

    protected override void OnResume()
    {
        base.OnResume();
        InvalidateOptionsMenu();
    }

    public override void OnBackPressed()
    {
        if (IsDirty)
        {
            UiHelper.Confirm(this, "Unsaved changes", "Leave without compiling your changes?", Finish);
            return;
        }
#pragma warning disable CA1422
        base.OnBackPressed();
#pragma warning restore CA1422
    }

    /// <summary>
    /// Wrapper so "Code entry properties" shows the entry's fields rather than opening this editor again.
    /// </summary>
    private sealed class CodeEntryView
    {
        private readonly UndertaleCode _code;

        public CodeEntryView(UndertaleCode code) => _code = code;

        public UndertaleString Name { get => _code.Name; set => _code.Name = value; }
        public uint Length => _code.Length;
        public uint LocalsCount { get => _code.LocalsCount; set => _code.LocalsCount = value; }
        public ushort ArgumentsCount { get => _code.ArgumentsCount; set => _code.ArgumentsCount = value; }
        public bool WeirdLocalFlag { get => _code.WeirdLocalFlag; set => _code.WeirdLocalFlag = value; }
        public uint Offset { get => _code.Offset; set => _code.Offset = value; }
        public UndertaleCode ParentEntry => _code.ParentEntry;
        public IList<UndertaleCode> ChildEntries => _code.ChildEntries;
        public int InstructionCount => _code.Instructions.Count;

        public override string ToString() => _code.Name?.Content ?? "code";
    }
}
