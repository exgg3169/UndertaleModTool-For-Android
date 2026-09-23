using Android.Content;
using Android.Content.Res;
using Android.Database;
using Android.Provider;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// File system helpers: bundled asset extraction, well-known directories and SAF utilities.
/// </summary>
public static class StorageHelper
{
    /// <summary>Directory the bundled scripts are extracted to.</summary>
    public static string ScriptsDirectory { get; private set; }

    /// <summary>Directory where UndertaleModLib's GameSpecificData lives.</summary>
    public static string LibDataDirectory { get; private set; }

    /// <summary>
    /// User-accessible working directory for script exports/imports
    /// (Android/data/&lt;package&gt;/files/UndertaleModTool on shared storage).
    /// </summary>
    public static string WorkDirectory { get; private set; }

    /// <summary>
    /// Extracts bundled assets into app storage, if not already done for this app version.
    /// </summary>
    public static void Initialize(Context context)
    {
        string filesDir = context.FilesDir!.AbsolutePath;
        ScriptsDirectory = Path.Combine(filesDir, "Scripts");
        LibDataDirectory = filesDir;

        string external = context.GetExternalFilesDir(null)?.AbsolutePath ?? filesDir;
        WorkDirectory = Path.Combine(external, "UndertaleModTool");
        Directory.CreateDirectory(WorkDirectory);

        long versionCode;
        try
        {
            var info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
            versionCode = OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
            versionCode ^= info.LastUpdateTime;
        }
        catch
        {
            versionCode = 0;
        }

        string stampFile = Path.Combine(filesDir, "assets.stamp");
        string stamp = versionCode.ToString();
        if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp && Directory.Exists(ScriptsDirectory))
            return;

        AssetManager assets = context.Assets!;
        foreach (string root in new[] { "Scripts", "GameSpecificData" })
        {
            string target = Path.Combine(filesDir, root);
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            CopyAssetTree(assets, root, target);
        }
        File.WriteAllText(stampFile, stamp);
    }

    private static void CopyAssetTree(AssetManager assets, string assetPath, string targetPath)
    {
        string[] children = assets.List(assetPath) ?? Array.Empty<string>();
        if (children.Length == 0)
        {
            // It's a file (or an empty directory, which assets never contain).
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using Stream input = assets.Open(assetPath);
            using FileStream output = File.Create(targetPath);
            input.CopyTo(output);
            return;
        }

        Directory.CreateDirectory(targetPath);
        foreach (string child in children)
            CopyAssetTree(assets, assetPath + "/" + child, Path.Combine(targetPath, child));
    }

    /// <summary>Returns the display name of a document, or null.</summary>
    public static string GetDisplayName(Context context, global::Android.Net.Uri uri)
    {
        try
        {
            using ICursor cursor = context.ContentResolver!.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
                return cursor.GetString(0);
        }
        catch
        {
            // Fall through
        }
        return uri.LastPathSegment;
    }

    /// <summary>Copies a document to a local file, returning the local path.</summary>
    public static string CopyUriToDirectory(Context context, global::Android.Net.Uri uri, string directory)
    {
        Directory.CreateDirectory(directory);
        string name = GetDisplayName(context, uri) ?? "file";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        string path = Path.Combine(directory, name);
        using Stream input = context.ContentResolver!.OpenInputStream(uri)!;
        using FileStream output = File.Create(path);
        input.CopyTo(output);
        return path;
    }

    /// <summary>Human-readable form of a path, shortening the shared storage prefix.</summary>
    public static string Pretty(string path)
    {
        const string prefix = "/storage/emulated/0/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? "Internal storage/" + path[prefix.Length..] : path;
    }
}
