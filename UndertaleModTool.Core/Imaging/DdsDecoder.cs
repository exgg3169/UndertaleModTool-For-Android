using System.Buffers.Binary;
using System.Numerics;

namespace UndertaleModTool.Core.Imaging;

/// <summary>
/// A managed DDS decoder for the formats GameMaker texture pages use: DXT1/BC1, DXT3/BC2, DXT5/BC3
/// and uncompressed 24/32-bit RGB(A) (including DX10 headers). Only the top mip level is decoded.
/// </summary>
public static class DdsDecoder
{
    public static bool IsDds(ReadOnlySpan<byte> data) => data.Length >= 128 && data[..4].SequenceEqual("DDS "u8);

    public static ArgbImage Decode(ReadOnlySpan<byte> data)
    {
        if (!IsDds(data))
            throw new InvalidDataException("Not a DDS file.");

        int height = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
        ReadOnlySpan<byte> fourCC = data.Slice(84, 4);
        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(data[88..]);
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(data[92..]);
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(data[96..]);
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(data[100..]);
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(data[104..]);

        const uint DDPF_ALPHAPIXELS = 0x1, DDPF_FOURCC = 0x4;
        int offset = 128;
        string format;
        if ((pfFlags & DDPF_FOURCC) != 0)
        {
            format = System.Text.Encoding.ASCII.GetString(fourCC);
            if (format == "DX10")
            {
                if (data.Length < 148)
                    throw new InvalidDataException("Truncated DX10 DDS header.");
                uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(data[128..]);
                offset = 148;
                (format, rMask, gMask, bMask, aMask, bitCount) = dxgi switch
                {
                    70 or 71 or 72 => ("DXT1", 0u, 0u, 0u, 0u, 0),
                    73 or 74 or 75 => ("DXT3", 0u, 0u, 0u, 0u, 0),
                    76 or 77 or 78 => ("DXT5", 0u, 0u, 0u, 0u, 0),
                    27 or 28 or 29 => ("RGB", 0x000000FFu, 0x0000FF00u, 0x00FF0000u, 0xFF000000u, 32),
                    87 or 91 => ("RGB", 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u, 32),
                    88 or 93 => ("RGB", 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0u, 32),
                    _ => throw new NotSupportedException($"Unsupported DDS DXGI format {dxgi}."),
                };
            }
        }
        else
        {
            format = "RGB";
            if ((pfFlags & DDPF_ALPHAPIXELS) == 0)
                aMask = 0;
        }

        ArgbImage image = new(width, height);
        ReadOnlySpan<byte> pixels = data[offset..];
        switch (format)
        {
            case "DXT1":
                DecodeBlocks(pixels, image, 8, (block, output) => DecodeColorBlock(block, output, allowTransparent: true));
                break;
            case "DXT2":
            case "DXT3":
                DecodeBlocks(pixels, image, 16, (block, output) =>
                {
                    DecodeColorBlock(block[8..], output, allowTransparent: false);
                    for (int i = 0; i < 16; i++)
                    {
                        int a4 = (block[i / 2] >> ((i & 1) * 4)) & 0xF;
                        output[i] = (output[i] & 0x00FFFFFF) | ((a4 * 17) << 24);
                    }
                });
                break;
            case "DXT4":
            case "DXT5":
                DecodeBlocks(pixels, image, 16, (block, output) =>
                {
                    DecodeColorBlock(block[8..], output, allowTransparent: false);
                    Span<int> alphas = stackalloc int[8];
                    int a0 = block[0], a1 = block[1];
                    alphas[0] = a0;
                    alphas[1] = a1;
                    if (a0 > a1)
                    {
                        for (int i = 1; i < 7; i++)
                            alphas[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
                    }
                    else
                    {
                        for (int i = 1; i < 5; i++)
                            alphas[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
                        alphas[6] = 0;
                        alphas[7] = 255;
                    }
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++)
                        bits |= (ulong)block[2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++)
                    {
                        int index = (int)((bits >> (3 * i)) & 7);
                        output[i] = (output[i] & 0x00FFFFFF) | (alphas[index] << 24);
                    }
                });
                break;
            case "RGB":
                DecodeUncompressed(pixels, image, bitCount, rMask, gMask, bMask, aMask);
                break;
            default:
                throw new NotSupportedException($"Unsupported DDS format \"{format}\".");
        }
        return image;
    }

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<int> output);

    private static void DecodeBlocks(ReadOnlySpan<byte> data, ArgbImage image, int blockSize, BlockDecoder decode)
    {
        int blocksX = (image.Width + 3) / 4, blocksY = (image.Height + 3) / 4;
        if (data.Length < blocksX * blocksY * blockSize)
            throw new InvalidDataException("Truncated DDS image data.");
        Span<int> output = stackalloc int[16];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                decode(data.Slice((by * blocksX + bx) * blockSize, blockSize), output);
                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= image.Height)
                        break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x < image.Width)
                            image.Pixels[y * image.Width + x] = output[py * 4 + px];
                    }
                }
            }
        }
    }

    private static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<int> output, bool allowTransparent)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Span<int> colors = stackalloc int[4];
        Expand565(c0, out int r0, out int g0, out int b0);
        Expand565(c1, out int r1, out int g1, out int b1);
        colors[0] = Argb(255, r0, g0, b0);
        colors[1] = Argb(255, r1, g1, b1);
        if (c0 > c1 || !allowTransparent)
        {
            colors[2] = Argb(255, (2 * r0 + r1 + 1) / 3, (2 * g0 + g1 + 1) / 3, (2 * b0 + b1 + 1) / 3);
            colors[3] = Argb(255, (r0 + 2 * r1 + 1) / 3, (g0 + 2 * g1 + 1) / 3, (b0 + 2 * b1 + 1) / 3);
        }
        else
        {
            colors[2] = Argb(255, (r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
            colors[3] = 0;
        }
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int i = 0; i < 16; i++)
            output[i] = colors[(int)((indices >> (2 * i)) & 3)];
    }

    private static void Expand565(ushort c, out int r, out int g, out int b)
    {
        r = (c >> 11) & 31;
        g = (c >> 5) & 63;
        b = c & 31;
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
    }

    private static int Argb(int a, int r, int g, int b) => (a << 24) | (r << 16) | (g << 8) | b;

    private static void DecodeUncompressed(ReadOnlySpan<byte> data, ArgbImage image, int bitCount, uint rMask, uint gMask, uint bMask, uint aMask)
    {
        if (bitCount is not (16 or 24 or 32))
            throw new NotSupportedException($"Unsupported uncompressed DDS bit count {bitCount}.");
        int bytes = bitCount / 8;
        if (data.Length < image.Width * image.Height * bytes)
            throw new InvalidDataException("Truncated DDS image data.");
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            uint v = 0;
            for (int k = 0; k < bytes; k++)
                v |= (uint)data[i * bytes + k] << (8 * k);
            int a = aMask == 0 ? 255 : Channel(v, aMask);
            image.Pixels[i] = Argb(a, Channel(v, rMask), Channel(v, gMask), Channel(v, bMask));
        }
    }

    private static int Channel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        int shift = BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint v = (value & mask) >> shift;
        return (int)(v * 255 / max);
    }
}
