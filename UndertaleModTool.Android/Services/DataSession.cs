using System.Collections.Concurrent;
using System.Text;
using Android.Content;
using UndertaleModLib;
using UndertaleModLib.Decompiler;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// Process-wide state of the currently opened data file.
/// </summary>
/// <remarks>
/// Android activities cannot pass arbitrary .NET objects to each other through intents,
/// so objects that are being navigated to are parked in <see cref="Park"/> and fetched
/// back with <see cref="Fetch"/> using the returned handle.
/// </remarks>
public static class DataSession
{
    /// <summary>The loaded data file, or null.</summary>
    public static UndertaleData Data { get; private set; }

    /// <summary>Local (app-private) path of the loaded data file.</summary>
    public static string FilePath { get; private set; }

    /// <summary>The document the data file was opened from, if any; used for "Save".</summary>
    public static global::Android.Net.Uri SourceUri { get; private set; }

    /// <summary>User-visible name of the loaded file.</summary>
    public static string DisplayName { get; private set; }

    /// <summary>Whether the data was modified since it was loaded or last saved.</summary>
    public static bool IsModified { get; set; }

    /// <summary>Warnings reported while loading the file.</summary>
    public static List<string> LoadWarnings { get; } = new();

    /// <summary>Object most recently selected by the user (exposed to scripts as "Selected").</summary>
    public static object Selected { get; set; }

    private static GlobalDecompileContext _decompileContext;

    /// <summary>A decompile context shared by all editors for the loaded data.</summary>
    public static GlobalDecompileContext DecompileContext
    {
        get
        {
            if (Data is null)
                return null;
            return _decompileContext ??= new GlobalDecompileContext(Data);
        }
    }

    /// <summary>Drops cached decompiler state, e.g. after code was recompiled.</summary>
    public static void InvalidateDecompileContext() => _decompileContext = null;

    /// <summary>Raised on the calling thread whenever <see cref="Data"/> is replaced.</summary>
    public static event Action DataChanged;

    public static void SetData(UndertaleData data, string filePath, global::Android.Net.Uri sourceUri, string displayName)
    {
        Data?.Dispose();
        Data = data;
        FilePath = filePath;
        SourceUri = sourceUri;
        DisplayName = displayName;
        IsModified = false;
        Selected = null;
        _decompileContext = null;
        Parked.Clear();
        DataChanged?.Invoke();
    }

    public static void Close()
    {
        SetData(null, null, null, null);
        LoadWarnings.Clear();
    }

    /// <summary>
    /// Copies a document into app storage and parses it as a GameMaker data file.
    /// </summary>
    public static UndertaleData LoadFromUri(Context context, global::Android.Net.Uri uri, Action<string> onMessage)
    {
        string displayName = StorageHelper.GetDisplayName(context, uri) ?? "data.win";
        string localDir = Path.Combine(context.CacheDir!.AbsolutePath, "opened");
        Directory.CreateDirectory(localDir);
        foreach (string old in Directory.GetFiles(localDir))
            File.Delete(old);
        string localPath = Path.Combine(localDir, displayName);

        onMessage?.Invoke("Copying file...");
        using (Stream input = context.ContentResolver!.OpenInputStream(uri)!)
        using (FileStream output = File.Create(localPath))
            input.CopyTo(output);

        UndertaleData data = LoadFromFile(localPath, onMessage);
        SetData(data, localPath, uri, displayName);
        return data;
    }

    /// <summary>
    /// Parses a GameMaker data file located on the file system.
    /// </summary>
    public static UndertaleData LoadFromFile(string path, Action<string> onMessage)
    {
        LoadWarnings.Clear();
        onMessage?.Invoke("Reading data file...");
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(stream,
            (warning, isImportant) =>
            {
                lock (LoadWarnings)
                    LoadWarnings.Add((isImportant ? "[!] " : "") + warning);
            },
            message => onMessage?.Invoke(message));
    }

    /// <summary>
    /// Serializes the loaded data to a temporary file and then copies it to <paramref name="uri"/>.
    /// </summary>
    public static void SaveToUri(Context context, global::Android.Net.Uri uri, Action<string> onMessage)
    {
        if (Data is null)
            throw new InvalidOperationException("No data file is loaded.");

        string tempPath = Path.Combine(context.CacheDir!.AbsolutePath, "save.tmp");
        onMessage?.Invoke("Serializing data...");
        using (FileStream fs = File.Create(tempPath))
            UndertaleIO.Write(fs, Data, message => onMessage?.Invoke(message));

        onMessage?.Invoke("Writing file...");
        using (FileStream fs = File.OpenRead(tempPath))
        using (Stream output = context.ContentResolver!.OpenOutputStream(uri, "wt")!)
            fs.CopyTo(output);
        File.Delete(tempPath);

        SourceUri = uri;
        DisplayName = StorageHelper.GetDisplayName(context, uri) ?? DisplayName;
        IsModified = false;
    }

    /// <summary>
    /// Serializes the loaded data to a path on the file system (used by scripts).
    /// </summary>
    public static void SaveToFile(string path, Action<string> onMessage)
    {
        if (Data is null)
            throw new InvalidOperationException("No data file is loaded.");
        string tempPath = path + ".tmp";
        using (FileStream fs = File.Create(tempPath))
            UndertaleIO.Write(fs, Data, message => onMessage?.Invoke(message));
        File.Move(tempPath, path, true);
    }

    #region Object handles

    private static readonly ConcurrentDictionary<int, object> Parked = new();
    private static int _nextHandle = 1;

    /// <summary>Stores an object and returns a handle that can be passed through an Intent.</summary>
    public static int Park(object obj)
    {
        int handle = Interlocked.Increment(ref _nextHandle);
        Parked[handle] = obj;
        return handle;
    }

    /// <summary>Returns the object stored with <see cref="Park"/>, or null.</summary>
    public static object Fetch(int handle) => Parked.TryGetValue(handle, out object obj) ? obj : null;

    #endregion

    /// <summary>Short summary of the loaded file for the main screen.</summary>
    public static string Describe()
    {
        if (Data is null)
            return "No data file loaded.\n\nUse \"Open\" to pick a data.win / game.unx / game.ios / game.droid file " +
                   "(for Android games, it is \"assets/game.droid\" inside the APK).";

        var gen = Data.GeneralInfo;
        StringBuilder sb = new();
        sb.AppendLine(DisplayName + (IsModified ? "  (modified)" : ""));
        if (gen is not null)
        {
            sb.AppendLine($"Game: {gen.DisplayName?.Content ?? gen.Name?.Content}");
            sb.AppendLine($"GameMaker version: {gen.Major}.{gen.Minor}.{gen.Release}.{gen.Build}" +
                          (Data.IsGameMaker2() ? " (GMS2)" : ""));
            sb.AppendLine($"Bytecode version: {gen.BytecodeVersion}");
        }
        if (Data.IsYYC())
            sb.AppendLine("YYC compiled: code is not available in this file.");
        if (LoadWarnings.Count > 0)
            sb.AppendLine($"{LoadWarnings.Count} warning(s) while loading - see menu > Load warnings.");
        return sb.ToString().TrimEnd();
    }
}
