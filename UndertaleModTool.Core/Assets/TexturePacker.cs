using System.Numerics;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Imaging;

namespace UndertaleModTool.Core.Assets;

/// <summary>
/// Packs images onto new texture pages (like UTMT's ImportGraphics packer): transparent borders are
/// trimmed (kept as the item's target offset), images are placed with a simple shelf algorithm, and a
/// texture page item is created for each.
/// </summary>
public static class TexturePacker
{
    /// <summary>Largest page size to create.</summary>
    public const int MaxPageSize = 2048;

    /// <summary>Space between images on a page, to avoid bleeding when filtered.</summary>
    public const int Padding = 2;

    /// <summary>Returns the bounds of non-transparent pixels, or null if the image is fully transparent.</summary>
    public static (int X, int Y, int Width, int Height)? OpaqueBounds(ArgbImage image)
    {
        int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if ((image.Pixels[y * image.Width + x] >>> 24) == 0)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Packs images and returns a texture page item for each (same order).
    /// </summary>
    public static List<UndertaleTexturePageItem> Pack(UndertaleData data, IReadOnlyList<ArgbImage> images, bool trim = true)
    {
        // Trim
        var entries = images.Select((image, index) =>
        {
            var bounds = trim ? OpaqueBounds(image) ?? (0, 0, 1, 1) : (0, 0, image.Width, image.Height);
            ArgbImage content = bounds == (0, 0, image.Width, image.Height) ? image : image.Crop(bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4);
            return (Index: index, Original: image, Content: content, Offset: (bounds.Item1, bounds.Item2));
        }).ToList();

        foreach (var e in entries)
        {
            if (e.Content.Width + Padding > MaxPageSize || e.Content.Height + Padding > MaxPageSize)
                throw new ArgumentException($"Image {e.Index} ({e.Content.Width}x{e.Content.Height}) is larger than the maximum texture page size ({MaxPageSize}).");
        }

        GMImage.ImageFormat format = data.EmbeddedTextures
            .Select(t => t?.TextureData?.Image?.Format)
            .FirstOrDefault(f => f is not null and not GMImage.ImageFormat.Dds and not GMImage.ImageFormat.Unknown)
            ?? GMImage.ImageFormat.Png;

        UndertaleTexturePageItem[] result = new UndertaleTexturePageItem[images.Count];
        var remaining = entries.OrderByDescending(e => e.Content.Height).ThenByDescending(e => e.Content.Width).ToList();
        while (remaining.Count > 0)
        {
            // Shelf-pack as many images as fit on one page.
            List<(int Entry, int X, int Y)> placed = new();
            int shelfX = 0, shelfY = 0, shelfHeight = 0, usedW = 0, usedH = 0;
            List<int> leftover = new();
            for (int i = 0; i < remaining.Count; i++)
            {
                var content = remaining[i].Content;
                int w = content.Width + Padding, h = content.Height + Padding;
                if (shelfX + w > MaxPageSize)
                {
                    shelfY += shelfHeight;
                    shelfX = 0;
                    shelfHeight = 0;
                }
                if (shelfY + h > MaxPageSize)
                {
                    leftover.Add(i);
                    continue;
                }
                placed.Add((i, shelfX, shelfY));
                shelfX += w;
                shelfHeight = Math.Max(shelfHeight, h);
                usedW = Math.Max(usedW, shelfX);
                usedH = Math.Max(usedH, shelfY + h);
            }

            int pageW = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, usedW));
            int pageH = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, usedH));
            ArgbImage page = new(pageW, pageH);
            foreach (var (entry, x, y) in placed)
                page.Paste(remaining[entry].Content, x, y);

            UndertaleEmbeddedTexture texture = new() { Name = new UndertaleString($"Texture {data.EmbeddedTextures.Count}") };
            texture.TextureData.Image = ImageCodec.ToGMImage(page, format);
            data.EmbeddedTextures.Add(texture);

            foreach (var (entry, x, y) in placed)
            {
                var e = remaining[entry];
                UndertaleTexturePageItem item = new()
                {
                    Name = new UndertaleString($"PageItem {data.TexturePageItems.Count}"),
                    SourceX = (ushort)x,
                    SourceY = (ushort)y,
                    SourceWidth = (ushort)e.Content.Width,
                    SourceHeight = (ushort)e.Content.Height,
                    TargetX = (ushort)e.Offset.Item1,
                    TargetY = (ushort)e.Offset.Item2,
                    TargetWidth = (ushort)e.Content.Width,
                    TargetHeight = (ushort)e.Content.Height,
                    BoundingWidth = (ushort)e.Original.Width,
                    BoundingHeight = (ushort)e.Original.Height,
                    TexturePage = texture,
                };
                data.TexturePageItems.Add(item);
                result[e.Index] = item;
            }

            remaining = leftover.Select(i => remaining[i]).ToList();
        }
        return result.ToList();
    }
}
