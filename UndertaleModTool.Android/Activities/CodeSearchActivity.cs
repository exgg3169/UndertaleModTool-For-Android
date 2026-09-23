using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// Searches decompiled code of all code entries, or displays search results produced by a script.
/// </summary>
[Activity(Label = "Search in code", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class CodeSearchActivity : BaseActivity
{
    public const string ExtraResults = "results";

    public sealed record ResultSet(string Title, string Query, int Count,
                                   List<KeyValuePair<string, List<(int lineNum, string codeLine)>>> Results,
                                   List<string> Failed);

    private EditText _query;
    private CheckBox _caseSensitive, _regex;
    private TextView _summary;
    private ListView _list;
    private readonly List<(string CodeName, int Line, string Text)> _rows = new();

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.SetDisplayHomeAsUpEnabled(true);

        LinearLayout root = UiHelper.VerticalLayout(this, 8);
        var preset = DataSession.Fetch(Intent!.GetIntExtra(ExtraResults, 0)) as ResultSet;

        if (preset is null)
        {
            LinearLayout row = new(this) { Orientation = Orientation.Horizontal };
            _query = new EditText(this) { Hint = "Text to find" };
            _query.SetSingleLine(true);
            row.AddView(_query, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            row.AddView(UiHelper.Button(this, "Search", Search));
            root.AddView(row);

            LinearLayout options = new(this) { Orientation = Orientation.Horizontal };
            _caseSensitive = new CheckBox(this) { Text = "Case sensitive" };
            _regex = new CheckBox(this) { Text = "Regex" };
            options.AddView(_caseSensitive);
            options.AddView(_regex);
            root.AddView(options);
        }

        _summary = UiHelper.Label(this, "", 13);
        root.AddView(_summary);
        _list = new ListView(this) { FastScrollEnabled = true };
        _list.ItemClick += (_, e) => OpenRow(e.Position);
        root.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        SetContentView(root);
        UiHelper.FitSystemWindows(root);

        if (preset is not null)
        {
            Title = preset.Title ?? "Search results";
            ShowResults(preset.Results, preset.Failed, preset.Query);
        }
    }

    private void Search()
    {
        var data = DataSession.Data;
        string query = _query.Text ?? "";
        if (data is null || query.Length == 0)
            return;
        if (data.IsYYC())
        {
            UiHelper.ShowMessage(this, "YYC", "This game has no bytecode to search (YYC).");
            return;
        }

        Regex regex = null;
        if (_regex.Checked)
        {
            try
            {
                regex = new Regex(query, _caseSensitive.Checked ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            catch (ArgumentException e)
            {
                UiHelper.Toast(this, "Invalid regex: " + e.Message);
                return;
            }
        }
        StringComparison comparison = _caseSensitive.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        UiHelper.RunWithProgress(this, "Searching", report =>
        {
            var codes = data.Code.Where(c => c is not null && c.ParentEntry is null).ToList();
            ConcurrentDictionary<string, List<(int, string)>> results = new();
            ConcurrentBag<string> failed = new();
            int done = 0;
            var context = DataSession.DecompileContext;

            Parallel.ForEach(codes, code =>
            {
                string text = CodeHelper.Decompile(data, code, context);
                string name = code.Name?.Content ?? "?";
                if (text.StartsWith("/*\nDECOMPILER FAILED!", StringComparison.Ordinal))
                    failed.Add(name);

                List<(int, string)> lines = null;
                int lineNum = 0;
                foreach (string line in text.Split('\n'))
                {
                    lineNum++;
                    bool match = regex?.IsMatch(line) ?? line.Contains(query, comparison);
                    if (match)
                        (lines ??= new()).Add((lineNum, line.Trim()));
                }
                if (lines is not null)
                    results[name] = lines;

                int n = Interlocked.Increment(ref done);
                if (n % 50 == 0)
                    report($"Decompiled {n}/{codes.Count} code entries...");
            });
            return (results.OrderBy(r => r.Key, StringComparer.Ordinal).ToList(), failed.ToList());
        }, result => ShowResults(result.Item1, result.Item2, query));
    }

    private void ShowResults(List<KeyValuePair<string, List<(int lineNum, string codeLine)>>> results, List<string> failed, string query)
    {
        _rows.Clear();
        int total = 0;
        foreach (var (codeName, lines) in results)
        {
            _rows.Add((codeName, 0, $"▶ {codeName}  ({lines.Count})"));
            foreach (var (lineNum, codeLine) in lines)
            {
                _rows.Add((codeName, lineNum, $"    {lineNum}: {codeLine}"));
                total++;
            }
        }
        _summary.Text = $"{total} result(s) in {results.Count} code entries for \"{query}\"" +
                        (failed is { Count: > 0 } ? $"; {failed.Count} entries failed to decompile" : "");
        _list.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1, _rows.Select(r => r.Text).ToList());
    }

    private void OpenRow(int position)
    {
        var (codeName, line, _) = _rows[position];
        UndertaleCode code = DataSession.Data?.Code.ByName(codeName);
        if (code is null)
        {
            UiHelper.Toast(this, "Code entry not found: " + codeName);
            return;
        }
        Intent intent = new(this, typeof(CodeEditorActivity));
        intent.PutExtra(Navigator.ExtraHandle, DataSession.Park(code));
        intent.PutExtra(CodeEditorActivity.ExtraLine, line);
        StartActivity(intent);
    }
}
