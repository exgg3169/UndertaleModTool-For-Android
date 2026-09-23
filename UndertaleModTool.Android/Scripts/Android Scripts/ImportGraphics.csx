// Imports PNG images, without ImageMagick (Android version of Resource Importers/ImportGraphics.csx).
//   name.png      -> replaces the background or font called "name"
//   sprite_N.png  -> frame N of sprite "sprite" (created if it doesn't exist)
// Images are trimmed and packed onto new texture pages, so their size can change.
using System.Linq;
using System.Threading.Tasks;
using UndertaleModTool.Core.Assets;
using UndertaleModTool.Core.Imaging;

EnsureDataLoaded();

string folder = PromptChooseDirectory();
if (folder is null)
    return;

bool recursive = ScriptQuestion("Also import images from subfolders?");
string[] files = Directory.GetFiles(folder, "*.png", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
if (files.Length == 0)
{
    ScriptError("No PNG files found in " + folder);
    return;
}

SetProgressBar(null, "Reading images", 0, files.Length);
StartProgressBarUpdater();
var images = new List<(string, ArgbImage)>();
List<string> unreadable = new();
await Task.Run(() =>
{
    foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
    {
        try
        {
            images.Add((Path.GetFileNameWithoutExtension(file), PngDecoder.Decode(file)));
        }
        catch (Exception e)
        {
            unreadable.Add($"{Path.GetFileName(file)}: {e.Message}");
        }
        IncrementProgress();
    }
});
await StopProgressBarUpdater();
HideProgressBar();

var report = AssetTools.ImportImages(Data, images);
ScriptMessage($"Imported from {files.Length} file(s): {report}" +
              (unreadable.Count > 0 ? $"\nCould not read {unreadable.Count}:\n" + string.Join("\n", unreadable.Take(10)) : ""));
