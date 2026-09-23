using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;
using UndertaleModTool.Core.Mcp;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// Controls the MCP server that lets AI assistants (Claude Code, Claude Desktop, ...) use the app.
/// </summary>
[Activity(Label = "AI assistant (MCP)", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden,
          WindowSoftInputMode = SoftInput.AdjustResize)]
public class McpActivity : BaseActivity
{
    private Switch _running;
    private EditText _port;
    private CheckBox _remote;
    private TextView _token, _addresses, _log;
    private bool _updating;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.SetDisplayHomeAsUpEnabled(true);

        ScrollView scroll = new(this);
        LinearLayout layout = UiHelper.VerticalLayout(this, 16);
        scroll.AddView(layout);

        layout.AddView(UiHelper.Label(this,
            "Lets an AI assistant work on the loaded game for you: read and change code, strings, sprites, rooms and sounds, " +
            "and run scripts. It speaks the Model Context Protocol (MCP), so it works with Claude Code, Claude Desktop and other MCP clients.\n\n" +
            "Connect from a terminal on this phone (e.g. Claude Code in Termux), from a computer over USB (adb forward) " +
            "or over Wi-Fi. Anyone with the access token can control the app, so keep it private.", 13));

        _running = new Switch(this) { Text = "Server running" };
        _running.SetPadding(0, this.Dp(16), 0, this.Dp(8));
        _running.CheckedChange += (_, e) =>
        {
            if (_updating)
                return;
            if (e.IsChecked)
                StartServer();
            else
                McpService.Stop(this);
            _running.PostDelayed(Refresh, 600);
        };
        layout.AddView(_running);

        layout.AddView(UiHelper.Header(this, "Port"));
        _port = new EditText(this) { Text = McpSettings.Port.ToString(), InputType = InputTypes.ClassNumber };
        layout.AddView(_port);

        _remote = new CheckBox(this) { Text = "Allow network access (other devices on the same Wi-Fi)", Checked = McpSettings.AllowRemote };
        layout.AddView(_remote);

        layout.AddView(UiHelper.Header(this, "Access token"));
        _token = UiHelper.Label(this, McpSettings.Token, 15);
        _token.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Bold);
        _token.SetTextIsSelectable(true);
        layout.AddView(_token);
        LinearLayout tokenButtons = new(this) { Orientation = Orientation.Horizontal };
        tokenButtons.AddView(UiHelper.Button(this, "Copy token", () => Copy("Token", McpSettings.Token)));
        tokenButtons.AddView(UiHelper.Button(this, "New token", () =>
            UiHelper.Confirm(this, "New token", "Clients using the old token will stop working. Continue?", () =>
            {
                McpSettings.Token = McpHttpServer.CreateToken();
                if (McpService.Server is not null)
                    McpService.Server.Token = McpSettings.Token;
                Refresh();
            })));
        layout.AddView(tokenButtons);

        layout.AddView(UiHelper.Header(this, "Connect"));
        _addresses = UiHelper.Label(this, "", 13);
        _addresses.SetTextIsSelectable(true);
        layout.AddView(_addresses);
        layout.AddView(UiHelper.Button(this, "Copy Claude Code command", () => Copy("Claude Code command", ClaudeCommand())));
        layout.AddView(UiHelper.Button(this, "Copy JSON config (Claude Desktop, Cursor, ...)", () => Copy("MCP config", JsonConfig())));
        layout.AddView(UiHelper.Button(this, "Copy adb forward command (USB)", () => Copy("adb", $"adb forward tcp:{CurrentPort} tcp:{CurrentPort}")));
        layout.AddView(UiHelper.Button(this, "Open web console", () =>
        {
            try
            {
                StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse($"http://127.0.0.1:{CurrentPort}/#token={McpSettings.Token}")));
            }
            catch (Exception e)
            {
                UiHelper.Toast(this, e.Message);
            }
        }));

        layout.AddView(UiHelper.Header(this, "Activity"));
        _log = UiHelper.Label(this, "", 11);
        _log.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        _log.SetTextIsSelectable(true);
        layout.AddView(_log);

        SetContentView(scroll);
        UiHelper.FitSystemWindows(scroll);
    }

    private int CurrentPort => McpService.Server?.Port ?? (int.TryParse(_port.Text, out int p) ? p : McpSettings.Port);

    private string Endpoint => $"http://127.0.0.1:{CurrentPort}/mcp";

    private string ClaudeCommand()
        => $"claude mcp add --transport http undertalemodtool {Endpoint} --header \"Authorization: Bearer {McpSettings.Token}\"";

    private string JsonConfig() =>
        "{\n  \"mcpServers\": {\n    \"undertalemodtool\": {\n      \"command\": \"npx\",\n" +
        $"      \"args\": [\"-y\", \"mcp-remote\", \"{Endpoint}\", \"--header\", \"Authorization: Bearer {McpSettings.Token}\"]\n" +
        "    }\n  }\n}";

    private void Copy(string label, string text)
    {
        var clipboard = (global::Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
        clipboard.PrimaryClip = ClipData.NewPlainText(label, text);
        UiHelper.Toast(this, label + " copied");
    }

    private void StartServer()
    {
        if (!int.TryParse(_port.Text, out int port) || port is < 1024 or > 65535)
        {
            _port.Error = "Use a port between 1024 and 65535";
            _updating = true;
            _running.Checked = false;
            _updating = false;
            return;
        }
        McpSettings.Port = port;
        McpSettings.AllowRemote = _remote.Checked;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 1);
        }
        McpService.Start(this);
    }

    protected override void OnResume()
    {
        base.OnResume();
        McpService.LogAdded += OnLog;
        Refresh();
    }

    protected override void OnPause()
    {
        McpService.LogAdded -= OnLog;
        base.OnPause();
    }

    private void OnLog(string line) => RunOnUiThread(Refresh);

    private void Refresh()
    {
        McpHttpServer server = McpService.Server;
        _updating = true;
        _running.Checked = server is not null;
        _updating = false;
        _port.Enabled = _remote.Enabled = server is null;
        _token.Text = McpSettings.Token;

        string text;
        if (server is null)
        {
            text = "Server is stopped.";
        }
        else
        {
            text = $"On this phone / over USB:\n  {Endpoint}\n";
            if (server.AllowRemote)
            {
                var addresses = McpSettings.LocalAddresses();
                text += addresses.Count == 0
                    ? "Network: no Wi-Fi address found.\n"
                    : "Over Wi-Fi:\n" + string.Join("\n", addresses.Select(a => $"  http://{a}:{server.Port}/mcp")) + "\n";
            }
            text += $"\nClaude Code:\n  {ClaudeCommand()}";
        }
        _addresses.Text = text;
        _log.Text = string.Join("\n", McpService.Log.Reverse().Take(60));
    }
}
