using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using UndertaleModTool.Android.Activities;
using UndertaleModTool.Core.Mcp;

namespace UndertaleModTool.Android.Services;

/// <summary>Persistent MCP server settings.</summary>
public static class McpSettings
{
    public const int DefaultPort = 8765;

    private static ISharedPreferences Prefs => Application.Context.GetSharedPreferences("mcp", FileCreationMode.Private)!;

    public static int Port
    {
        get => Prefs.GetInt("port", DefaultPort);
        set => Prefs.Edit()!.PutInt("port", value)!.Apply();
    }

    public static bool AllowRemote
    {
        get => Prefs.GetBoolean("remote", false);
        set => Prefs.Edit()!.PutBoolean("remote", value)!.Apply();
    }

    public static string Token
    {
        get
        {
            string token = Prefs.GetString("token", null);
            if (string.IsNullOrEmpty(token))
            {
                token = McpHttpServer.CreateToken();
                Token = token;
            }
            return token;
        }
        set => Prefs.Edit()!.PutString("token", value)!.Apply();
    }

    /// <summary>IPv4 addresses of the device on local networks (Wi-Fi, hotspot, ...).</summary>
    public static List<string> LocalAddresses()
    {
        List<string> result = new();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                        result.Add(address.Address.ToString());
                }
            }
        }
        catch
        {
            // Not available on some devices.
        }
        return result;
    }
}

/// <summary>
/// Foreground service hosting the MCP server, so AI clients can keep working while the app is in the background.
/// </summary>
[Register("com.underminers.undertalemodtool.McpService")]
public class McpService : Service
{
    public const string ActionStart = "com.underminers.undertalemodtool.MCP_START";
    public const string ActionStop = "com.underminers.undertalemodtool.MCP_STOP";
    private const int NotificationId = 1;
    private const string ChannelId = "mcp";

    private static readonly List<string> RecentLog = new();

    /// <summary>The running server, or null.</summary>
    public static McpHttpServer Server { get; private set; }

    /// <summary>Raised (on any thread) for each log line.</summary>
    public static event Action<string> LogAdded;

    public static string[] Log
    {
        get
        {
            lock (RecentLog)
                return RecentLog.ToArray();
        }
    }

    private static void AddLog(string line)
    {
        line = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (RecentLog)
        {
            RecentLog.Add(line);
            if (RecentLog.Count > 200)
                RecentLog.RemoveAt(0);
        }
        LogAdded?.Invoke(line);
    }

    public static void Start(Context context)
    {
        Intent intent = new(context, typeof(McpService));
        intent.SetAction(ActionStart);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop(Context context)
    {
        Intent intent = new(context, typeof(McpService));
        intent.SetAction(ActionStop);
        context.StartService(intent);
    }

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopServer();
            StopForegroundCompat();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Must go foreground promptly after startForegroundService().
        StartForegroundCompat(BuildNotification("Starting..."));
        try
        {
            StopServer();
            McpServer mcp = UmtTools.Create(new AndroidUmtHost(this), PackageVersion());
            mcp.Log += AddLog;
            McpHttpServer http = new(mcp) { Token = McpSettings.Token, HomePage = WebConsole.Html };
            http.Log += AddLog;
            http.Start(McpSettings.Port, McpSettings.AllowRemote);
            Server = http;
            string where = McpSettings.AllowRemote
                ? string.Join(", ", McpSettings.LocalAddresses().Select(a => $"{a}:{http.Port}").DefaultIfEmpty($"port {http.Port}"))
                : $"127.0.0.1:{http.Port}";
            UpdateNotification($"Listening on {where}");
        }
        catch (Exception e)
        {
            AddLog("Failed to start: " + e.Message);
            Server = null;
            StopForegroundCompat();
            StopSelf();
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        StopServer();
        base.OnDestroy();
    }

    private static void StopServer()
    {
        if (Server is null)
            return;
        Server.Stop();
        Server = null;
        AddLog("Server stopped");
    }

    private string PackageVersion()
    {
        try
        {
            return PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName ?? "1.0";
        }
        catch
        {
            return "1.0";
        }
    }

    #region Notification

    private Notification BuildNotification(string text)
    {
        NotificationManager manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && manager.GetNotificationChannel(ChannelId) is null)
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "AI assistant server (MCP)", NotificationImportance.Low));

        Intent open = new(this, typeof(McpActivity));
        PendingIntent openIntent = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        Intent stop = new(this, typeof(McpService));
        stop.SetAction(ActionStop);
        PendingIntent stopIntent = PendingIntent.GetService(this, 1, stop, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

#pragma warning disable CA1422
        Notification.Builder builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
#pragma warning restore CA1422
        builder.SetSmallIcon(Resource.Drawable.ic_notification)
               .SetContentTitle("UndertaleModTool MCP server")
               .SetContentText(text)
               .SetOngoing(true)
               .SetContentIntent(openIntent)
               .AddAction(new Notification.Action.Builder(null, "Stop", stopIntent).Build());
        return builder.Build();
    }

    private void UpdateNotification(string text)
        => ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, BuildNotification(text));

    private void StartForegroundCompat(Notification notification)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }

    private void StopForegroundCompat()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Remove);
    }

    #endregion
}
