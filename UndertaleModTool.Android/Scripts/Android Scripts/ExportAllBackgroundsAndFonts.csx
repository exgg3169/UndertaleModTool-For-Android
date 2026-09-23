// Exports all backgrounds/tilesets and font sheets as PNG, without ImageMagick.
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Imaging;

EnsureDataLoaded();

string folder = PromptChooseDirectory();
if (folder is null)
    return;

TexturePageCache cache = new();
int count = 0;
List<string> errors = new();

void Export(string kind, string name, UndertaleTexturePageItem item)
{
    if (item is null)
        return;
    string dir = Paths.JoinVerifyWithinDirectory(folder, kind);
    Directory.CreateDirectory(dir);
    try
    {
        ImageCodec.ExportPageItemPng(item, Paths.JoinVerifyWithinDirectory(dir, name + ".png"), true, cache);
        count++;
    }
    catch (Exception e)
    {
        errors.Add($"{name}: {e.Message}");
    }
}

await Task.Run(() =>
{
    foreach (UndertaleBackground bg in Data.Backgrounds.Where(b => b is not null))
        Export("Backgrounds", bg.Name.Content, bg.Texture);
    foreach (UndertaleFont font in Data.Fonts.Where(f => f is not null))
        Export("Fonts", font.Name.Content, font.Texture);
});

ScriptMessage($"Exported {count} image(s) to {folder}" + (errors.Count > 0 ? $"\n{errors.Count} failed:\n" + string.Join("\n", errors.Take(10)) : ""));
