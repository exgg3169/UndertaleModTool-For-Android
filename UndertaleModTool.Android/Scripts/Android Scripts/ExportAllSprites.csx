// Exports every sprite frame as PNG, without ImageMagick (Android version of
// Resource Exporters/ExportAllSprites.csx). Uses UndertaleModTool.Core's managed image codecs.
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Imaging;

EnsureDataLoaded();

string folder = PromptChooseDirectory();
if (folder is null)
    return;

bool padded = ScriptQuestion("Export sprites with padding?");
bool useSubDirectories = ScriptQuestion("Export sprites into subdirectories?");

// Group frames by texture page so each page is decoded only once.
var frames = Data.Sprites
    .Where(s => s is { SSpriteType: UndertaleSprite.SpriteType.Normal, Textures.Count: > 0 })
    .SelectMany(s => s.Textures.Select((t, i) => (Sprite: s, Frame: i, Item: t?.Texture)))
    .Where(f => f.Item?.TexturePage is not null)
    .GroupBy(f => f.Item.TexturePage)
    .ToList();

int total = frames.Sum(g => g.Count()), done = 0, failed = 0;
SetProgressBar(null, "Exporting sprites", 0, total);
StartProgressBarUpdater();

await Task.Run(() =>
{
    foreach (var page in frames)
    {
        TexturePageCache cache = new(); // one decoded page at a time keeps memory low
        foreach (var (sprite, frame, item) in page)
        {
            string dir = useSubDirectories ? Paths.JoinVerifyWithinDirectory(folder, sprite.Name.Content) : folder;
            Directory.CreateDirectory(dir);
            try
            {
                ImageCodec.ExportPageItemPng(item, Paths.JoinVerifyWithinDirectory(dir, $"{sprite.Name.Content}_{frame}.png"), padded, cache);
            }
            catch (Exception e)
            {
                failed++;
                SetUMTConsoleText($"{sprite.Name.Content}_{frame}: {e.Message}");
            }
            IncrementProgress();
            done++;
        }
    }
});

await StopProgressBarUpdater();
HideProgressBar();
ScriptMessage($"Exported {done - failed} sprite frame(s) to {folder}" + (failed > 0 ? $" ({failed} failed)" : ""));
