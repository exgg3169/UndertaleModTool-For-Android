using System.Collections;
using System.Numerics;
using System.Text.RegularExpressions;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Imaging;

namespace UndertaleModTool.Core.Assets;

/// <summary>
/// Adding and replacing sprites frames and sounds, following UTMT's ImportGraphics and
/// ImportSingleSound scripts, without ImageMagick.
/// </summary>
public static class AssetTools
{
    /// <summary>
    /// Adds a frame to a sprite. The image is scaled to the sprite's size if needed, and gets its own
    /// new texture page (in the same image format as the game's existing pages).
    /// </summary>
    /// <returns>The new frame index.</returns>
    public static int AddSpriteFrame(UndertaleData data, UndertaleSprite sprite, ArgbImage image)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        int width = sprite.Width > 0 ? (int)sprite.Width : image.Width;
        int height = sprite.Height > 0 ? (int)sprite.Height : image.Height;
        if (sprite.Width == 0 || sprite.Height == 0)
        {
            sprite.Width = (uint)width;
            sprite.Height = (uint)height;
        }
        ArgbImage frame = image.Resize(width, height);
        int framesBefore = sprite.Textures.Count;

        UndertaleTexturePageItem item = TexturePacker.Pack(data, new[] { frame })[0];
        sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = item });

        // Sprites with one collision mask per frame need a mask for the new frame too; reuse the last one.
        if (framesBefore > 1 && sprite.CollisionMasks.Count == framesBefore)
        {
            var last = sprite.CollisionMasks[^1];
            sprite.CollisionMasks.Add(new UndertaleSprite.MaskEntry((byte[])last.Data.Clone(), last.Width, last.Height));
        }
        return sprite.Textures.Count - 1;
    }

    #region Graphics import

    /// <summary>What <see cref="ImportImages"/> did.</summary>
    public sealed class ImportReport
    {
        public List<string> Replaced { get; } = new();
        public List<string> Created { get; } = new();
        public List<string> Skipped { get; } = new();

        public override string ToString()
            => $"{Replaced.Count} replaced, {Created.Count} new sprite(s), {Skipped.Count} skipped" +
               (Skipped.Count > 0 ? ":\n  " + string.Join("\n  ", Skipped) : "");
    }

    private static readonly Regex SpriteFrameName = new(@"^(.+?)_(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// Imports images by name, like UTMT's ImportGraphics: "name.png" replaces the background or font
    /// called "name"; "sprite_N.png" is frame N of sprite "sprite" (sprites are created if missing).
    /// Images are trimmed and packed onto new texture pages, so their size may change freely.
    /// </summary>
    public static ImportReport ImportImages(UndertaleData data, IEnumerable<(string Name, ArgbImage Image)> images, bool createSprites = true)
    {
        ImportReport report = new();
        List<(string Name, ArgbImage Image, Action<UndertaleTexturePageItem> Apply)> jobs = new();
        bool bboxMasks = data.IsVersionAtLeast(2024, 6);

        foreach (var (name, image) in images)
        {
            if (data.Backgrounds?.ByName(name) is UndertaleBackground background)
            {
                jobs.Add((name, image, item =>
                {
                    background.Texture = item;
                    report.Replaced.Add("background " + name);
                }));
                continue;
            }
            if (data.Fonts?.ByName(name) is UndertaleFont font)
            {
                jobs.Add((name, image, item =>
                {
                    font.Texture = item;
                    report.Replaced.Add("font " + name);
                }));
                continue;
            }

            string spriteName = name;
            int frame = 0;
            Match match = SpriteFrameName.Match(name);
            if (match.Success && data.Sprites.ByName(name) is null)
            {
                spriteName = match.Groups[1].Value;
                frame = int.Parse(match.Groups[2].Value);
            }
            UndertaleSprite sprite = data.Sprites.ByName(spriteName);
            if (sprite is null && !(createSprites && match.Success))
            {
                report.Skipped.Add($"{name}: no background, font or sprite with this name (sprite frames are named sprite_N)");
                continue;
            }

            int capturedFrame = frame;
            string capturedName = spriteName;
            jobs.Add((name, image, item =>
            {
                UndertaleSprite target = data.Sprites.ByName(capturedName);
                if (target is null)
                {
                    target = new UndertaleSprite
                    {
                        Name = data.Strings.MakeString(capturedName),
                        Width = item.BoundingWidth,
                        Height = item.BoundingHeight,
                        MarginLeft = item.TargetX,
                        MarginRight = item.TargetX + item.TargetWidth - 1,
                        MarginTop = item.TargetY,
                        MarginBottom = item.TargetY + item.TargetHeight - 1,
                        OriginX = 0,
                        OriginY = 0,
                    };
                    data.Sprites.Add(target);
                    report.Created.Add(capturedName);
                }
                SetSpriteFrame(data, target, capturedFrame, item, image, bboxMasks);
                report.Replaced.Add($"{capturedName} frame {capturedFrame}");
            }));
        }

        if (jobs.Count == 0)
            return report;
        List<UndertaleTexturePageItem> items = TexturePacker.Pack(data, jobs.Select(j => j.Image).ToList());
        for (int i = 0; i < jobs.Count; i++)
            jobs[i].Apply(items[i]);
        return report;
    }

    /// <summary>
    /// Sets a sprite frame to a new texture page item, growing the frame list, sprite size and bounding
    /// box as needed and updating the collision mask like UTMT's ImportGraphics.
    /// </summary>
    private static void SetSpriteFrame(UndertaleData data, UndertaleSprite sprite, int frame, UndertaleTexturePageItem item, ArgbImage fullImage, bool bboxMasks)
    {
        UndertaleSprite.TextureEntry entry = new() { Texture = item };
        if (frame >= sprite.Textures.Count)
        {
            while (sprite.Textures.Count <= frame)
                sprite.Textures.Add(entry);
        }
        else
        {
            sprite.Textures[frame] = entry;
        }

        uint oldWidth = sprite.Width, oldHeight = sprite.Height;
        sprite.Width = item.BoundingWidth;
        sprite.Height = item.BoundingHeight;
        bool changedDimensions = oldWidth != sprite.Width || oldHeight != sprite.Height;

        bool grewBoundingBox = false;
        if (sprite.BBoxMode != 2)
        {
            bool full = sprite.BBoxMode == 1;
            int left = full ? 0 : item.TargetX;
            int right = full ? (int)sprite.Width - 1 : item.TargetX + item.TargetWidth - 1;
            int top = full ? 0 : item.TargetY;
            int bottom = full ? (int)sprite.Height - 1 : item.TargetY + item.TargetHeight - 1;
            if (left < sprite.MarginLeft) { sprite.MarginLeft = left; grewBoundingBox = true; }
            if (top < sprite.MarginTop) { sprite.MarginTop = top; grewBoundingBox = true; }
            if (right > sprite.MarginRight) { sprite.MarginRight = right; grewBoundingBox = true; }
            if (bottom > sprite.MarginBottom) { sprite.MarginBottom = bottom; grewBoundingBox = true; }
        }

        bool basicRectangle = sprite.SepMasks is UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect;
        bool needsMask = !bboxMasks || !basicRectangle || sprite.CollisionMasks.Count > 0;
        if (needsMask && ((bboxMasks && grewBoundingBox) ||
                          (sprite.SepMasks is UndertaleSprite.SepMaskType.Precise && sprite.CollisionMasks.Count == 0) ||
                          (!bboxMasks && changedDimensions) ||
                          sprite.CollisionMasks.Count == 0))
        {
            sprite.CollisionMasks.Clear();
            sprite.CollisionMasks.Add(CreateMask(data, sprite, fullImage));
        }
    }

    /// <summary>
    /// Creates a collision mask from an image's alpha channel (opaque where alpha &gt; 0). The image is
    /// the full (untrimmed) frame; for GameMaker 2024.6+ the mask covers only the bounding box.
    /// </summary>
    public static UndertaleSprite.MaskEntry CreateMask(UndertaleData data, UndertaleSprite sprite, ArgbImage fullImage)
    {
        UndertaleSprite.MaskEntry mask = sprite.NewMaskEntry(data);
        bool bboxMasks = data.IsVersionAtLeast(2024, 6);
        int offsetX = bboxMasks ? sprite.MarginLeft : 0, offsetY = bboxMasks ? sprite.MarginTop : 0;
        int stride = (mask.Width + 7) / 8;
        for (int y = 0; y < mask.Height; y++)
        {
            int iy = y + offsetY;
            if (iy < 0 || iy >= fullImage.Height)
                continue;
            for (int x = 0; x < mask.Width; x++)
            {
                int ix = x + offsetX;
                if (ix < 0 || ix >= fullImage.Width || (fullImage.Pixels[iy * fullImage.Width + ix] >>> 24) == 0)
                    continue;
                mask.Data[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }
        return mask;
    }

    #endregion

    /// <summary>
    /// Creates a new texture page containing just <paramref name="image"/> and a texture page item for it.
    /// </summary>
    public static UndertaleTexturePageItem CreatePageWithItem(UndertaleData data, ArgbImage image)
    {
        int pageW = (int)BitOperations.RoundUpToPowerOf2((uint)image.Width);
        int pageH = (int)BitOperations.RoundUpToPowerOf2((uint)image.Height);
        ArgbImage page = new(pageW, pageH);
        page.Paste(image, 0, 0);

        GMImage.ImageFormat format = data.EmbeddedTextures
            .Select(t => t?.TextureData?.Image?.Format)
            .FirstOrDefault(f => f is not null and not GMImage.ImageFormat.Dds and not GMImage.ImageFormat.Unknown)
            ?? GMImage.ImageFormat.Png;

        UndertaleEmbeddedTexture texture = new()
        {
            Name = new UndertaleString($"Texture {data.EmbeddedTextures.Count}"),
        };
        texture.TextureData.Image = ImageCodec.ToGMImage(page, format);
        data.EmbeddedTextures.Add(texture);

        UndertaleTexturePageItem item = new()
        {
            Name = new UndertaleString($"PageItem {data.TexturePageItems.Count}"),
            SourceX = 0,
            SourceY = 0,
            SourceWidth = (ushort)image.Width,
            SourceHeight = (ushort)image.Height,
            TargetX = 0,
            TargetY = 0,
            TargetWidth = (ushort)image.Width,
            TargetHeight = (ushort)image.Height,
            BoundingWidth = (ushort)image.Width,
            BoundingHeight = (ushort)image.Height,
            TexturePage = texture,
        };
        data.TexturePageItems.Add(item);
        return item;
    }

    public static string DetectAudioExtension(byte[] data)
    {
        if (data.Length >= 4)
        {
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
                return ".wav";
            if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
                return ".ogg";
            if ((data[0] == 'I' && data[1] == 'D' && data[2] == '3') || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0))
                return ".mp3";
        }
        return ".bin";
    }

    private static UndertaleSound.AudioEntryFlags FlagsFor(string ext, bool decodeOnLoad)
    {
        var flags = UndertaleSound.AudioEntryFlags.Regular;
        if (ext == ".wav")
            return flags | UndertaleSound.AudioEntryFlags.IsEmbedded;
        flags |= UndertaleSound.AudioEntryFlags.IsCompressed;
        if (decodeOnLoad)
            flags |= UndertaleSound.AudioEntryFlags.IsEmbedded;
        return flags;
    }

    private static void EnsureWavOrOgg(string ext)
    {
        if (ext is not (".wav" or ".ogg"))
            throw new InvalidDataException("Only WAV and OGG files can be used as GameMaker sounds.");
    }

    /// <summary>
    /// Adds a new sound embedded in the data file (in the built-in audio group).
    /// </summary>
    public static UndertaleSound AddSound(UndertaleData data, string name, byte[] audio)
    {
        if (data.Sounds.Any(s => s?.Name?.Content == name))
            throw new InvalidOperationException($"A sound named \"{name}\" already exists.");
        string ext = DetectAudioExtension(audio);
        EnsureWavOrOgg(ext);

        UndertaleEmbeddedAudio embedded = new()
        {
            Name = new UndertaleString($"EmbeddedSound {data.EmbeddedAudio.Count}"),
            Data = audio,
        };
        data.EmbeddedAudio.Add(embedded);

        int builtinGroup = data.GetBuiltinSoundGroupID();
        UndertaleSound sound = new()
        {
            Name = data.Strings.MakeString(name),
            Flags = FlagsFor(ext, decodeOnLoad: true),
            Type = data.Strings.MakeString(ext),
            File = data.Strings.MakeString(name + ext),
            Effects = 0,
            Volume = 1.0f,
            Pitch = 1.0f,
            AudioFile = embedded,
            // Also set the raw ID: UndertaleSound writes it instead of the reference when the
            // built-in audio group isn't group 0 (early GMS1 layouts).
            AudioID = data.EmbeddedAudio.Count - 1,
            AudioGroup = data.AudioGroups is { Count: > 0 } groups && builtinGroup < groups.Count ? groups[builtinGroup] : null,
            GroupID = builtinGroup,
        };
        data.Sounds.Add(sound);
        return sound;
    }

    /// <summary>
    /// Embeds audio into a sound that currently has none in the data file (e.g. an external .ogg
    /// next to the game), making it a regular embedded sound of the built-in audio group.
    /// </summary>
    public static void EmbedSoundAudio(UndertaleData data, UndertaleSound sound, byte[] audio)
    {
        string ext = DetectAudioExtension(audio);
        EnsureWavOrOgg(ext);
        UndertaleEmbeddedAudio embedded = new()
        {
            Name = new UndertaleString($"EmbeddedSound {data.EmbeddedAudio.Count}"),
            Data = audio,
        };
        data.EmbeddedAudio.Add(embedded);
        int builtinGroup = data.GetBuiltinSoundGroupID();
        sound.AudioFile = embedded;
        sound.AudioID = data.EmbeddedAudio.Count - 1;
        if (data.AudioGroups is { Count: > 0 } groups && builtinGroup < groups.Count)
            sound.AudioGroup = groups[builtinGroup];
        sound.GroupID = builtinGroup;
        sound.Flags = FlagsFor(ext, decodeOnLoad: true);
        sound.Type = data.Strings.MakeString(ext);
    }

    /// <summary>
    /// Replaces the audio of an embedded sound in place (keeping its audio ID), updating its flags and type.
    /// </summary>
    /// <param name="data">The main data file (for strings).</param>
    /// <param name="sound">The sound.</param>
    /// <param name="embedded">The sound's embedded audio entry (in the data file or an audio group file).</param>
    /// <param name="audio">New WAV or OGG data.</param>
    public static void ReplaceSoundAudio(UndertaleData data, UndertaleSound sound, UndertaleEmbeddedAudio embedded, byte[] audio)
    {
        string ext = DetectAudioExtension(audio);
        EnsureWavOrOgg(ext);
        embedded.Data = audio;
        sound.Flags = FlagsFor(ext, decodeOnLoad: ext == ".wav" || sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded));
        sound.Type = data.Strings.MakeString(ext);
    }
}
