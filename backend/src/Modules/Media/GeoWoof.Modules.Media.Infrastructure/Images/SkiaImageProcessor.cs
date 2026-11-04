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

        if ((long)codec.Info.Width * codec.Info.Height > maxPixels)
        {
            return Rejected(MediaErrors.ImageTooLargeCode);
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return Rejected(MediaErrors.InvalidImageCode);
        }

        using var upright = ApplyOrigin(decoded, codec.EncodedOrigin);
        var (mediumWidth, mediumHeight) = FitWithin(upright, MediumSize);
        return new ImageProcessingResult(
            new ImageVariants(mediumWidth, mediumHeight, Encode(upright, ThumbSize), Encode(upright, MediumSize)),
            RejectionCode: null);
    }

    private static ImageProcessingResult Rejected(string code) => new(Variants: null, code);

    private static byte[] Encode(SKBitmap source, int maxSize)
    {
        var (width, height) = FitWithin(source, maxSize);
        if (width == source.Width && height == source.Height)
        {
            using var unscaled = source.Encode(SKEncodedImageFormat.Webp, WebPQuality);
            return unscaled.ToArray();
        }

        using var resized = source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), Sampling)
            ?? throw new InvalidOperationException("The image could not be resized.");
        using var encoded = resized.Encode(SKEncodedImageFormat.Webp, WebPQuality);
        return encoded.ToArray();
    }

    /// <summary>The size that fits <paramref name="maxSize"/> on the longest side. Images are never enlarged.</summary>
    private static (int Width, int Height) FitWithin(SKBitmap source, int maxSize)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxSize)
        {
            return (source.Width, source.Height);
        }

        var scale = (double)maxSize / longest;
        return (Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
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
