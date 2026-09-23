using ImageMagick;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using UndertaleModTool.Core.Imaging;

namespace UndertaleModTool.Core.Tests;

/// <summary>
/// Image codec tests. ImageMagick (which works on desktop, but not on Android) is the reference.
/// </summary>
public static class ImagingTests
{
    private static readonly Random Rnd = new(1);

    public static void Run(TestContext t)
    {
        using MagickImage source = RandomMagickImage(37, 29);

        foreach (var (name, setup) in PngVariants())
        {
            using MagickImage m = (MagickImage)source.Clone();
            setup(m);
            m.Format = MagickFormat.Png;
            byte[] file = m.ToByteArray();
            t.Check(MaxDiff(PngDecoder.Decode(file), MagickDecode(file)) == 0, $"PNG decode {name}");
        }

        ArgbImage random = RandomImage(50, 40);
        t.Check(PngDecoder.Decode(ImageCodec.EncodePng(random)).Pixels.SequenceEqual(random.Pixels), "own PNG encoder -> own decoder");
        t.Check(MaxDiff(random, MagickDecode(ImageCodec.EncodePng(random))) == 0, "own PNG encoder -> ImageMagick");

        foreach (var (name, tolerance) in new[] { ("dxt1", 8), ("dxt3", 8), ("dxt5", 8), ("none", 0) })
        {
            using MagickImage m = (MagickImage)source.Clone();
            m.Settings.SetDefine(MagickFormat.Dds, "compression", name);
            m.Settings.SetDefine(MagickFormat.Dds, "mipmaps", "0");
            m.Format = MagickFormat.Dds;
            byte[] file = m.ToByteArray();
            int diff = MaxDiff(DdsDecoder.Decode(file), MagickDecode(file));
            t.Check(diff <= tolerance, $"DDS decode {name} (max difference {diff})");
        }

        foreach (var format in new[] { GMImage.ImageFormat.Png, GMImage.ImageFormat.Qoi, GMImage.ImageFormat.Bz2Qoi, GMImage.ImageFormat.RawBgra })
        {
            GMImage image = ImageCodec.ToGMImage(random, format);
            t.Check(image.Format == format && ImageCodec.TryDecodeManaged(image).Pixels.SequenceEqual(random.Pixels), $"GMImage {format} round trip");
        }

        foreach (var format in new[] { GMImage.ImageFormat.Bz2Qoi, GMImage.ImageFormat.Png })
        {
            ArgbImage page = RandomImage(64, 64);
            UndertaleEmbeddedTexture texture = new();
            texture.TextureData.Image = ImageCodec.ToGMImage(page, format);
            UndertaleTexturePageItem item = new() { TexturePage = texture, SourceX = 10, SourceY = 20, SourceWidth = 8, SourceHeight = 6 };
            ArgbImage replacement = RandomImage(8, 6);
            ImageCodec.ReplacePageItem(item, replacement);
            ArgbImage expected = new(64, 64, (int[])page.Pixels.Clone());
            expected.Paste(replacement, 10, 20);
            t.Check(texture.TextureData.Image.Format == format && ImageCodec.DecodePage(texture).Pixels.SequenceEqual(expected.Pixels),
                    $"ReplacePageItem keeps {format} and pastes exactly");
        }

        {
            ArgbImage page = RandomImage(64, 64);
            UndertaleEmbeddedTexture texture = new();
            texture.TextureData.Image = ImageCodec.ToGMImage(page, GMImage.ImageFormat.Png);
            UndertaleTexturePageItem item = new()
            {
                TexturePage = texture, SourceX = 3, SourceY = 4, SourceWidth = 10, SourceHeight = 12,
                TargetX = 2, TargetY = 1, TargetWidth = 10, TargetHeight = 12, BoundingWidth = 16, BoundingHeight = 16,
            };
            ArgbImage padded = ImageCodec.GetPageItemImage(item);
            t.Check(padded.Width == 16 && padded.Height == 16 && padded.Pixels[1 * 16 + 2] == page.Pixels[4 * 64 + 3] && padded.Pixels[0] == 0,
                    "GetPageItemImage with padding");
        }
    }

    private static IEnumerable<(string, Action<MagickImage>)> PngVariants()
    {
        static void Png(MagickImage m, string colorType, string bitDepth = null)
        {
            m.Settings.SetDefine(MagickFormat.Png, "color-type", colorType);
            if (bitDepth is not null)
                m.Settings.SetDefine(MagickFormat.Png, "bit-depth", bitDepth);
        }
        yield return ("RGBA 8-bit", m => Png(m, "6", "8"));
        yield return ("RGBA 16-bit", m => Png(m, "6", "16"));
        yield return ("RGB 8-bit", m => { m.Alpha(AlphaOption.Off); Png(m, "2", "8"); });
        yield return ("RGB 16-bit", m => { m.Alpha(AlphaOption.Off); Png(m, "2", "16"); });
        yield return ("gray 8-bit", m => { m.Alpha(AlphaOption.Off); m.Grayscale(); Png(m, "0", "8"); });
        yield return ("gray 16-bit", m => { m.Alpha(AlphaOption.Off); m.Grayscale(); Png(m, "0", "16"); });
        yield return ("gray 4-bit", m => { m.Alpha(AlphaOption.Off); m.Grayscale(); m.Quantize(new QuantizeSettings { Colors = 16, ColorSpace = ColorSpace.Gray }); Png(m, "0", "4"); });
        yield return ("gray 1-bit", m => { m.Alpha(AlphaOption.Off); m.Threshold(new Percentage(50)); Png(m, "0", "1"); });
        yield return ("gray+alpha 8-bit", m => { m.Grayscale(); Png(m, "4", "8"); });
        yield return ("palette 8-bit", m => { m.Quantize(new QuantizeSettings { Colors = 200 }); Png(m, "3", "8"); });
        yield return ("palette 4-bit", m => { m.Quantize(new QuantizeSettings { Colors = 12 }); Png(m, "3", "4"); });
        yield return ("palette 2-bit", m => { m.Quantize(new QuantizeSettings { Colors = 4 }); Png(m, "3", "2"); });
        yield return ("RGBA interlaced", m => { m.Settings.Interlace = Interlace.Png; Png(m, "6"); });
        yield return ("palette interlaced", m => { m.Settings.Interlace = Interlace.Png; m.Quantize(new QuantizeSettings { Colors = 12 }); Png(m, "3", "4"); });
        yield return ("RGB with tRNS", m =>
        {
            m.Alpha(AlphaOption.Off);
            m.Opaque(m.GetPixels().GetPixel(0, 0).ToColor(), MagickColors.Magenta);
            m.Transparent(MagickColors.Magenta);
            Png(m, "2");
        });
    }

    public static ArgbImage RandomImage(int width, int height)
    {
        ArgbImage image = new(width, height);
        for (int i = 0; i < image.Pixels.Length; i++)
            image.Pixels[i] = Rnd.Next();
        return image;
    }

    private static MagickImage RandomMagickImage(int width, int height)
    {
        MagickImage image = new(MagickColors.Transparent, (uint)width, (uint)height);
        byte[] pixels = new byte[width * height * 4];
        Rnd.NextBytes(pixels);
        image.ImportPixels(pixels, new PixelImportSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        return image;
    }

    private static ArgbImage MagickDecode(byte[] file)
    {
        using MagickImage m = new(file);
        m.Alpha(AlphaOption.Set);
        m.ColorSpace = ColorSpace.sRGB;
        m.Format = MagickFormat.Bgra;
        m.Depth = 8;
        return ImageCodec.FromBgra(m.ToByteArray(), (int)m.Width, (int)m.Height);
    }

    /// <summary>Largest channel difference, ignoring the color of fully transparent pixels.</summary>
    private static int MaxDiff(ArgbImage a, ArgbImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return int.MaxValue;
        int max = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            int pa = a.Pixels[i], pb = b.Pixels[i];
            if ((pa >>> 24) == 0 && (pb >>> 24) == 0)
                continue;
            for (int shift = 0; shift < 32; shift += 8)
                max = Math.Max(max, Math.Abs(((pa >> shift) & 255) - ((pb >> shift) & 255)));
        }
        return max;
    }
}
