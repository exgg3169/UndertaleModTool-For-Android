using Android.Graphics;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModTool.Android.Ui;

/// <summary>
/// Decodes GameMaker textures into Android bitmaps for previews.
/// </summary>
/// <remarks>
/// UndertaleModLib uses ImageMagick for image conversion, whose native library is not available
/// on Android. PNG pages are decoded by Android itself, and QOI / BZ2+QOI pages are decoded to raw
/// BGRA by UndertaleModLib's own (managed) QOI decoder.
/// </remarks>
public static class ImageHelper
{
    /// <summary>Largest dimension of preview bitmaps; bigger images are downscaled.</summary>
    private const int MaxPreviewSize = 2048;

    /// <summary>
    /// Returns a preview bitmap for a resource, or null if it has no (supported) image.
    /// </summary>
    public static Bitmap GetPreview(object obj)
    {
        return obj switch
        {
            UndertaleEmbeddedTexture texture => GetPage(texture, null),
            UndertaleTexturePageItem item => GetPageItem(item),
            UndertaleSprite sprite when sprite.Textures.Count > 0 => GetPageItem(sprite.Textures[0]?.Texture),
            UndertaleSprite.TextureEntry entry => GetPageItem(entry.Texture),
            UndertaleBackground background => GetPageItem(background.Texture),
            UndertaleFont font => GetPageItem(font.Texture),
            _ => null,
        };
    }

    public static bool HasPreview(object obj) => obj is UndertaleEmbeddedTexture or UndertaleTexturePageItem or UndertaleSprite
        or UndertaleSprite.TextureEntry or UndertaleBackground or UndertaleFont;

    public static Bitmap GetPageItem(UndertaleTexturePageItem item)
    {
        if (item?.TexturePage is null || item.SourceWidth == 0 || item.SourceHeight == 0)
            return null;

        Rect source = new(item.SourceX, item.SourceY, item.SourceX + item.SourceWidth, item.SourceY + item.SourceHeight);
        Bitmap region = GetPage(item.TexturePage, source);
        if (region is null)
            return null;

        int width = Math.Max((int)item.BoundingWidth, item.TargetX + item.TargetWidth);
        int height = Math.Max((int)item.BoundingHeight, item.TargetY + item.TargetHeight);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
            return region;

        Bitmap result = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using Canvas canvas = new(result);
        canvas.DrawBitmap(region, null,
            new Rect(item.TargetX, item.TargetY, item.TargetX + item.TargetWidth, item.TargetY + item.TargetHeight), null);
        region.Recycle();
        return result;
    }

    /// <summary>
    /// Decodes a texture page, or a region of it when <paramref name="region"/> is given.
    /// </summary>
    public static Bitmap GetPage(UndertaleEmbeddedTexture texture, Rect region)
    {
        GMImage image = texture?.TextureData?.Image;
        if (image is null)
            return null;

        switch (image.Format)
        {
            case GMImage.ImageFormat.Png:
            {
                byte[] png = image.ToSpan().ToArray();
                if (region is not null)
                {
                    using BitmapRegionDecoder decoder = OperatingSystem.IsAndroidVersionAtLeast(31)
                        ? BitmapRegionDecoder.NewInstance(png, 0, png.Length)!
#pragma warning disable CS0618
                        : BitmapRegionDecoder.NewInstance(png, 0, png.Length, false)!;
#pragma warning restore CS0618
                    return decoder.DecodeRegion(ClampRegion(region, decoder.Width, decoder.Height), null);
                }

                BitmapFactory.Options bounds = new() { InJustDecodeBounds = true };
                BitmapFactory.DecodeByteArray(png, 0, png.Length, bounds);
                BitmapFactory.Options options = new() { InSampleSize = SampleSize(bounds.OutWidth, bounds.OutHeight) };
                return BitmapFactory.DecodeByteArray(png, 0, png.Length, options);
            }
            case GMImage.ImageFormat.RawBgra:
            case GMImage.ImageFormat.Qoi:
            case GMImage.ImageFormat.Bz2Qoi:
            {
                GMImage raw = image.ConvertToRawBgra();
                return FromBgra(raw.GetRawImageData(), raw.Width, raw.Height,
                                region ?? new Rect(0, 0, raw.Width, raw.Height));
            }
            default:
                // DDS and unknown formats need ImageMagick.
                return null;
        }
    }

    private static Rect ClampRegion(Rect region, int width, int height)
        => new(Math.Clamp(region.Left, 0, width), Math.Clamp(region.Top, 0, height),
               Math.Clamp(region.Right, 0, width), Math.Clamp(region.Bottom, 0, height));

    private static Bitmap FromBgra(ReadOnlySpan<byte> bgra, int width, int height, Rect region)
    {
        region = ClampRegion(region, width, height);
        int step = region.Width() * region.Height() > MaxPreviewSize * MaxPreviewSize
            ? SampleSize(region.Width(), region.Height())
            : 1;
        int outW = Math.Max(1, region.Width() / step);
        int outH = Math.Max(1, region.Height() / step);
        int[] pixels = new int[outW * outH];
        for (int y = 0; y < outH; y++)
        {
            int srcRow = (region.Top + y * step) * width;
            for (int x = 0; x < outW; x++)
            {
                int i = (srcRow + region.Left + x * step) * 4;
                pixels[y * outW + x] = (bgra[i + 3] << 24) | (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
            }
        }
        return Bitmap.CreateBitmap(pixels, outW, outH, Bitmap.Config.Argb8888!)!;
    }

    private static int SampleSize(int width, int height)
    {
        int sample = 1;
        while (width / sample > MaxPreviewSize || height / sample > MaxPreviewSize)
            sample *= 2;
        return sample;
    }
}
