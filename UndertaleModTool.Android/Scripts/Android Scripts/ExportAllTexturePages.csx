// Exports every embedded texture page as PNG, without ImageMagick
// (Android version of Resource Exporters/ExportAllEmbeddedTextures.csx).
using System.Threading.Tasks;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Imaging;

EnsureDataLoaded();

string folder = PromptChooseDirectory();
if (folder is null)
    return;

SetProgressBar(null, "Exporting texture pages", 0, Data.EmbeddedTextures.Count);
StartProgressBarUpdater();
List<string> errors = new();
await Task.Run(() =>
{
    for (int i = 0; i < Data.EmbeddedTextures.Count; i++)
    {
        try
        {
            ImageCodec.DecodePage(Data.EmbeddedTextures[i]).SavePng(Paths.JoinVerifyWithinDirectory(folder, $"{i}.png"));
        }
        catch (Exception e)
        {
            errors.Add($"{i}: {e.Message}");
        }
        IncrementProgress();
    }
});
await StopProgressBarUpdater();
HideProgressBar();
ScriptMessage($"Exported {Data.EmbeddedTextures.Count - errors.Count} texture page(s) to {folder}" +
              (errors.Count > 0 ? $"\n{errors.Count} failed:\n" + string.Join("\n", errors) : ""));
