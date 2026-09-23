using System.Buffers.Binary;
using System.IO.Compression;

namespace UndertaleModTool.Core.Imaging;

/// <summary>
/// A managed PNG decoder (all standard color types and bit depths, tRNS, Adam7 interlacing).
/// </summary>
/// <remarks>
/// UndertaleModLib decodes PNG with ImageMagick, which isn't available on Android.
/// </remarks>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static ArgbImage Decode(string path) => Decode(File.ReadAllBytes(path));

    public static ArgbImage Decode(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data))
            throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[] palette = null, paletteAlpha = null;
        ushort[] transparentColor = null;
        using MemoryStream idat = new();

        int pos = 8;
        while (pos + 12 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[pos..]);
            if (length < 0 || pos + 12 + length > data.Length)
                throw new InvalidDataException("Truncated PNG chunk.");
            ReadOnlySpan<byte> type = data.Slice(pos + 4, 4);
            ReadOnlySpan<byte> body = data.Slice(pos + 8, length);
            pos += 12 + length;

            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                bitDepth = body[8];
                colorType = body[9];
                interlace = body[12];
                if (body[10] != 0 || body[11] != 0)
                    throw new InvalidDataException("Unsupported PNG compression/filter method.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = body.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                if (colorType == 3)
                {
                    paletteAlpha = body.ToArray();
                }
                else if (colorType == 0 && body.Length >= 2)
                {
                    transparentColor = new[] { BinaryPrimitives.ReadUInt16BigEndian(body) };
                }
                else if (colorType == 2 && body.Length >= 6)
                {
                    transparentColor = new[]
                    {
                        BinaryPrimitives.ReadUInt16BigEndian(body),
                        BinaryPrimitives.ReadUInt16BigEndian(body[2..]),
                        BinaryPrimitives.ReadUInt16BigEndian(body[4..]),
                    };
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unsupported PNG color type {colorType}."),
        };
        if (bitDepth is not (1 or 2 or 4 or 8 or 16) || (colorType is 2 or 4 or 6 && bitDepth < 8) || (colorType == 3 && bitDepth > 8))
            throw new InvalidDataException($"Invalid PNG bit depth {bitDepth} for color type {colorType}.");
        if (colorType == 3 && palette is null)
            throw new InvalidDataException("Palette PNG without a PLTE chunk.");

        ArgbImage image = new(width, height);
        int bitsPerPixel = channels * bitDepth;
        int filterBpp = Math.Max(1, bitsPerPixel / 8);

        idat.Position = 0;
        using ZLibStream zlib = new(idat, CompressionMode.Decompress);

        if (interlace == 0)
        {
            DecodePass(zlib, image, 0, 0, 1, 1, width, height, bitsPerPixel, filterBpp);
        }
        else
        {
            ReadOnlySpan<int> passes = stackalloc int[] { 0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2 };
            for (int p = 0; p < 7; p++)
            {
                int x0 = passes[p * 4], y0 = passes[p * 4 + 1], dx = passes[p * 4 + 2], dy = passes[p * 4 + 3];
                int passWidth = (width - x0 + dx - 1) / dx;
                int passHeight = (height - y0 + dy - 1) / dy;
                if (passWidth > 0 && passHeight > 0)
                    DecodePass(zlib, image, x0, y0, dx, dy, passWidth, passHeight, bitsPerPixel, filterBpp);
            }
        }

        // Convert the raw samples stored by DecodePass into ARGB.
        Finish(image, colorType, bitDepth, palette, paletteAlpha, transparentColor);
        return image;

        void DecodePass(Stream stream, ArgbImage target, int x0, int y0, int dx, int dy, int passWidth, int passHeight, int bpp, int fbpp)
        {
            int stride = (passWidth * bpp + 7) / 8;
            byte[] previous = new byte[stride];
            byte[] current = new byte[stride];
            for (int row = 0; row < passHeight; row++)
            {
                int filter = stream.ReadByte();
                if (filter < 0)
                    throw new InvalidDataException("Truncated PNG image data.");
                stream.ReadExactly(current);
                Unfilter(filter, current, previous, fbpp);

                int y = y0 + row * dy;
                for (int col = 0; col < passWidth; col++)
                    target.Pixels[y * target.Width + x0 + col * dx] = PackSamples(current, col, channels, bitDepth);
                (previous, current) = (current, previous);
            }
        }
    }

    private static void Unfilter(int filter, byte[] row, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int i = bpp; i < row.Length; i++)
                    row[i] += row[i - bpp];
                return;
            case 2:
                for (int i = 0; i < row.Length; i++)
                    row[i] += prev[i];
                return;
            case 3:
                for (int i = 0; i < row.Length; i++)
                    row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + prev[i]) >> 1);
                return;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                return;
            default:
                throw new InvalidDataException($"Invalid PNG filter type {filter}.");
        }
    }

    /// <summary>
    /// Temporarily packs a pixel's raw samples into an int: for 8/16-bit images the (high bytes of the)
    /// up to four channels, first channel in the top byte; for sub-byte depths the single sample value.
    /// </summary>
    private static int PackSamples(byte[] row, int col, int channels, int bitDepth)
    {
        if (bitDepth < 8)
        {
            int bitIndex = col * bitDepth;
            int b = row[bitIndex >> 3];
            int shift = 8 - bitDepth - (bitIndex & 7);
            return (b >> shift) & ((1 << bitDepth) - 1);
        }
        if (bitDepth == 8)
        {
            int o = col * channels;
            int v = 0;
            for (int c = 0; c < channels; c++)
                v |= row[o + c] << (8 * (3 - c));
            return v;
        }

        // 16-bit: output is 8 bits per channel, so keep the high bytes.
        int o16 = col * channels * 2;
        int v16 = 0;
        for (int c = 0; c < channels; c++)
            v16 |= row[o16 + c * 2] << (8 * (3 - c));
        return v16;
    }

    private static void Finish(ArgbImage image, int colorType, int bitDepth, byte[] palette, byte[] paletteAlpha, ushort[] transparent)
    {
        int[] px = image.Pixels;
        int maxSub = (1 << Math.Min(bitDepth, 8)) - 1;
        for (int i = 0; i < px.Length; i++)
        {
            int v = px[i];
            int a = 255, r, g, b;
            switch (colorType)
            {
                case 0: // gray
                {
                    int raw = bitDepth < 8 ? v : (v >>> 24) & 0xFF;
                    int gray = bitDepth < 8 ? raw * 255 / maxSub : raw;
                    r = g = b = gray;
                    if (transparent is not null && Matches(transparent[0], raw, bitDepth))
                        a = 0;
                    break;
                }
                case 2: // RGB
                    r = (v >>> 24) & 0xFF;
                    g = (v >> 16) & 0xFF;
                    b = (v >> 8) & 0xFF;
                    if (transparent is not null && Matches(transparent[0], r, bitDepth) &&
                        Matches(transparent[1], g, bitDepth) && Matches(transparent[2], b, bitDepth))
                    {
                        a = 0;
                    }
                    break;
                case 3: // palette
                {
                    int index = bitDepth < 8 ? v : (v >>> 24) & 0xFF;
                    if (index * 3 + 2 >= palette.Length)
                        throw new InvalidDataException("PNG palette index out of range.");
                    r = palette[index * 3];
                    g = palette[index * 3 + 1];
                    b = palette[index * 3 + 2];
                    if (paletteAlpha is not null && index < paletteAlpha.Length)
                        a = paletteAlpha[index];
                    break;
                }
                case 4: // gray + alpha
                    r = g = b = (v >>> 24) & 0xFF;
                    a = (v >> 16) & 0xFF;
                    break;
                default: // RGBA
                    r = (v >>> 24) & 0xFF;
                    g = (v >> 16) & 0xFF;
                    b = (v >> 8) & 0xFF;
                    a = v & 0xFF;
                    break;
            }
            px[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }
    }

    /// <summary>
    /// Compares a tRNS value with a sample. For 16-bit images only the high byte is kept, so
    /// the comparison is done on high bytes (may, in theory, match a near-identical color).
    /// </summary>
    private static bool Matches(ushort trns, int sample, int bitDepth)
        => bitDepth == 16 ? (trns >> 8) == sample : trns == sample;
}
