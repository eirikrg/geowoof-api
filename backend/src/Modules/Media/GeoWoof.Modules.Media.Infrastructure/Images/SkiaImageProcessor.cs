using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;
using SkiaSharp;

namespace GeoWoof.Modules.Media.Infrastructure.Images;

/// <summary>
/// Decodes the upload, which also proves that it is really an image, applies its EXIF orientation
/// and encodes new WebP files from the pixels alone. Nothing from the original file survives apart
/// from the pixels: no EXIF, no GPS coordinates and no other metadata.
/// </summary>
internal sealed class SkiaImageProcessor : IImageProcessor
{
    private const int ThumbSize = 320;
    private const int MediumSize = 1080;
    private const int WebPQuality = 80;

    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    public ImageProcessingResult Process(ReadOnlyMemory<byte> original, long maxPixels)
    {
        using var data = SKData.CreateCopy(original.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return Rejected(MediaErrors.InvalidImageCode);
        }

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if ((long)width * height > maxPixels)
        {
            return Rejected(MediaErrors.ImageTooLargeCode);
        }

        // Nothing larger than the medium variant is kept, so the whole picture is never needed: JPEG and WebP decode
        // straight to a fraction of their size, which takes a fraction of the time and of the memory.
        var medium = FitWithin(width, height, MediumSize);
        using var decoded = Decode(codec, medium);
        if (decoded is null)
        {
            return Rejected(MediaErrors.InvalidImageCode);
        }

        // Turned upright once it is small: the same pixels, with much less to move. Most are upright already.
        using var resized = decoded.Width == medium.Width && decoded.Height == medium.Height ? null : Resize(decoded, medium);
        var small = resized ?? decoded;
        using var turned = codec.EncodedOrigin == SKEncodedOrigin.TopLeft ? null : ApplyOrigin(small, codec.EncodedOrigin);
        var upright = turned ?? small;
        var thumb = FitWithin(upright.Width, upright.Height, ThumbSize);
        return new ImageProcessingResult(
            new ImageVariants(upright.Width, upright.Height, Encode(upright, thumb), Encode(upright, (upright.Width, upright.Height))),
            RejectionCode: null);
    }

    private static ImageProcessingResult Rejected(string code) => new(Variants: null, code);

    /// <summary>
    /// Decodes at the smallest size the codec offers that is still at least <paramref name="target"/>. A codec that
    /// cannot scale, such as PNG, decodes the whole picture, which is what the limit of pixels is for.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec, (int Width, int Height) target)
    {
        var info = codec.Info;
        // A sixteenth more than needed: JPEG rounds to the nearest eighth, which could be smaller than the target.
        var scale = Math.Min(1f, ((float)target.Width / info.Width) + (1f / 16));
        var size = codec.GetScaledDimensions(scale);
        if (size.Width < target.Width || size.Height < target.Height)
        {
            size = info.Size;
        }

        // As SKBitmap.Decode does: premultiplied alpha and no colour space.
        var alpha = info.AlphaType == SKAlphaType.Unpremul ? SKAlphaType.Premul : info.AlphaType;
        return SKBitmap.Decode(codec, new SKImageInfo(size.Width, size.Height, info.ColorType, alpha));
    }

    private static SKBitmap Resize(SKBitmap source, (int Width, int Height) size) =>
        source.Resize(new SKImageInfo(size.Width, size.Height, source.ColorType, source.AlphaType), Sampling)
            ?? throw new InvalidOperationException("The image could not be resized.");

    private static byte[] Encode(SKBitmap source, (int Width, int Height) size)
    {
        if (size.Width == source.Width && size.Height == source.Height)
        {
            using var unscaled = source.Encode(SKEncodedImageFormat.Webp, WebPQuality);
            return unscaled.ToArray();
        }

        using var resized = Resize(source, size);
        using var encoded = resized.Encode(SKEncodedImageFormat.Webp, WebPQuality);
        return encoded.ToArray();
    }

    /// <summary>The size that fits <paramref name="maxSize"/> on the longest side. Images are never enlarged.</summary>
    private static (int Width, int Height) FitWithin(int width, int height, int maxSize)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxSize)
        {
            return (width, height);
        }

        var scale = (double)maxSize / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Applies the EXIF orientation to the pixels, because the variants no longer carry it.</summary>
    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        int width = source.Width, height = source.Height;
        var swapsSides = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(swapsSides ? height : width, swapsSides ? width : height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(height, width);
                canvas.RotateDegrees(-90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width);
                canvas.RotateDegrees(-90);
                break;
        }

        // Rotations by whole quarter turns map pixels one to one: no interpolation is needed.
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Flush();
        return result;
    }
}
