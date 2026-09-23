using System.Buffers.Binary;
using System.IO.Compression;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// A decoded image: non-premultiplied 0xAARRGGBB pixels, row-major.
/// </summary>
public sealed class ArgbImage
{
    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public ArgbImage(int width, int height, int[] pixels = null)
    {
        if (width <= 0 || height <= 0 || width > GMImage.MaxImageDimension || height > GMImage.MaxImageDimension)
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid image size {width}x{height}");
        Width = width;
        Height = height;
        Pixels = pixels ?? new int[width * height];
        if (Pixels.Length != width * height)
            throw new ArgumentException("Pixel count doesn't match the image size", nameof(pixels));
    }

    /// <summary>Nearest-neighbour resize (keeps pixel art crisp).</summary>
    public ArgbImage Resize(int width, int height)
    {
        if (width == Width && height == Height)
            return this;
        ArgbImage result = new(width, height);
        for (int y = 0; y < height; y++)
        {
            int sy = (int)((long)y * Height / height);
            for (int x = 0; x < width; x++)
                result.Pixels[y * width + x] = Pixels[sy * Width + (int)((long)x * Width / width)];
        }
        return result;
    }

    /// <summary>Copies <paramref name="image"/> over this image at (x, y), replacing pixels (no blending).</summary>
    public void Paste(ArgbImage image, int x, int y)
    {
        for (int row = 0; row < image.Height; row++)
        {
            int ty = y + row;
            if (ty < 0 || ty >= Height)
                continue;
            int x0 = Math.Max(0, x), x1 = Math.Min(Width, x + image.Width);
            if (x1 <= x0)
                return;
            Array.Copy(image.Pixels, row * image.Width + (x0 - x), Pixels, ty * Width + x0, x1 - x0);
        }
    }
}

/// <summary>
/// Image conversions that don't depend on ImageMagick (which isn't available on Android).
/// </summary>
public static class ImageCodec
{
    /// <summary>
    /// Converts raw BGRA data (UndertaleModLib's raw format) to ARGB pixels.
    /// </summary>
    public static ArgbImage FromBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgbImage image = new(width, height);
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            int o = i * 4;
            image.Pixels[i] = (bgra[o + 3] << 24) | (bgra[o + 2] << 16) | (bgra[o + 1] << 8) | bgra[o];
        }
        return image;
    }

    /// <summary>
    /// Creates a <see cref="GMImage"/> in <paramref name="format"/> from ARGB pixels.
    /// </summary>
    public static GMImage ToGMImage(ArgbImage image, GMImage.ImageFormat format)
    {
        if (format is GMImage.ImageFormat.Png or GMImage.ImageFormat.Dds or GMImage.ImageFormat.Unknown)
        {
            // UndertaleModLib's own PNG conversion needs ImageMagick; encode it ourselves.
            // (DDS isn't writable by UndertaleModLib either; it writes PNG instead.)
            return GMImage.FromPng(EncodePng(image), true);
        }

        GMImage raw = new(image.Width, image.Height);
        Span<byte> data = raw.GetRawImageData();
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            int p = image.Pixels[i];
            int o = i * 4;
            data[o] = (byte)p;
            data[o + 1] = (byte)(p >> 8);
            data[o + 2] = (byte)(p >> 16);
            data[o + 3] = (byte)(p >>> 24);
        }

        return format switch
        {
            GMImage.ImageFormat.Qoi => raw.ConvertToQoi(),
            GMImage.ImageFormat.Bz2Qoi => raw.ConvertToBz2Qoi(),
            _ => raw,
        };
    }

    /// <summary>
    /// Decodes a texture page from any format that doesn't need platform decoders
    /// (raw, QOI, BZ2+QOI). Returns null for PNG/DDS.
    /// </summary>
    public static ArgbImage TryDecodeManaged(GMImage image)
    {
        if (image.Format is not (GMImage.ImageFormat.RawBgra or GMImage.ImageFormat.Qoi or GMImage.ImageFormat.Bz2Qoi))
            return null;
        GMImage raw = image.ConvertToRawBgra();
        return FromBgra(raw.GetRawImageData(), raw.Width, raw.Height);
    }

    /// <summary>
    /// Replaces the part of the texture page used by <paramref name="item"/> with <paramref name="image"/>,
    /// resized to the item's source size, keeping the page's image format.
    /// This mirrors <see cref="UndertaleTexturePageItem.ReplaceTexture"/> in UndertaleModLib.
    /// </summary>
    /// <param name="decodePage">Decodes the item's full texture page.</param>
    public static void ReplacePageItem(UndertaleTexturePageItem item, ArgbImage image, Func<UndertaleEmbeddedTexture, ArgbImage> decodePage)
    {
        if (item?.TexturePage is null)
            throw new InvalidOperationException("This texture page item has no texture page.");
        if (item.SourceWidth == 0 || item.SourceHeight == 0)
            throw new InvalidOperationException("This texture page item is empty (0x0).");

        ArgbImage resized = image.Resize(item.SourceWidth, item.SourceHeight);
        lock (item.TexturePage.TextureData)
        {
            GMImage current = item.TexturePage.TextureData.Image;
            ArgbImage page = decodePage(item.TexturePage)
                ?? throw new InvalidOperationException($"Can't decode texture page format {current.Format}.");
            page.Paste(resized, item.SourceX, item.SourceY);
            item.TexturePage.TextureData.Image = ToGMImage(page, current.Format);
        }

        item.TargetWidth = (ushort)image.Width;
        item.TargetHeight = (ushort)image.Height;
    }

    /// <summary>
    /// Replaces a whole texture page, keeping its image format.
    /// </summary>
    public static void ReplacePage(UndertaleEmbeddedTexture texture, ArgbImage image)
    {
        GMImage current = texture.TextureData.Image;
        texture.TextureData.Image = ToGMImage(image, current?.Format ?? GMImage.ImageFormat.Png);
    }

    #region PNG encoder

    /// <summary>
    /// Encodes a non-premultiplied RGBA PNG (lossless).
    /// </summary>
    public static byte[] EncodePng(ArgbImage image)
    {
        using MemoryStream png = new();
        png.Write(GMImage.MagicPng);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], image.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(png, "IHDR"u8, ihdr);

        using MemoryStream idat = new();
        using (ZLibStream zlib = new(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = image.Width * 4;
            byte[] row = new byte[stride + 1];
            byte[] prev = new byte[stride + 1];
            byte[] filtered = new byte[stride + 1];
            for (int y = 0; y < image.Height; y++)
            {
                row[0] = 0;
                for (int x = 0; x < image.Width; x++)
                {
                    int p = image.Pixels[y * image.Width + x];
                    int o = 1 + x * 4;
                    row[o] = (byte)(p >> 16);
                    row[o + 1] = (byte)(p >> 8);
                    row[o + 2] = (byte)p;
                    row[o + 3] = (byte)(p >>> 24);
                }

                // "Up" filter usually compresses texture pages noticeably better than none.
                filtered[0] = 2;
                for (int i = 1; i <= stride; i++)
                    filtered[i] = (byte)(row[i] - prev[i]);
                zlib.Write(filtered);
                (row, prev) = (prev, row);
            }
        }
        WriteChunk(png, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        stream.Write(header);
        stream.Write(type);
        stream.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(header, crc);
        stream.Write(header);
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    #endregion
}
