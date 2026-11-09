using System.Text;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;
using GeoWoof.Modules.Media.Infrastructure.Images;
using GeoWoof.TestSupport;
using SkiaSharp;

namespace GeoWoof.Modules.Media.UnitTests;

public sealed class SkiaImageProcessorTests
{
    private const long ManyPixels = 64_000_000;

    private readonly SkiaImageProcessor _processor = new();

    [Fact]
    public void The_test_photo_really_carries_the_metadata_that_must_disappear()
    {
        TestImages.Contains(TestImages.Jpeg(80, 40, exifOrientation: 1), TestImages.SecretMarker).ShouldBeTrue();
    }

    [Fact]
    public void Variants_keep_nothing_from_the_exif_block()
    {
        var variants = Process(TestImages.Jpeg(1600, 1200, exifOrientation: 1));

        foreach (var variant in new[] { variants.Thumb, variants.Medium })
        {
            TestImages.Contains(variant.Span, TestImages.SecretMarker).ShouldBeFalse();
            TestImages.Contains(variant.Span, "Exif").ShouldBeFalse();
            TestImages.Contains(variant.Span, "EXIF").ShouldBeFalse();
        }
    }

    [Fact]
    public void Variants_are_webp_and_fit_their_size()
    {
        var variants = Process(TestImages.Jpeg(1600, 1200));

        variants.Width.ShouldBe(1080);
        variants.Height.ShouldBe(810);
        var (thumbWidth, thumbHeight) = SizeOf(variants.Thumb);
        thumbWidth.ShouldBe(320);
        thumbHeight.ShouldBe(240);
        SizeOf(variants.Medium).ShouldBe((1080, 810));
        TestImages.Contains(variants.Thumb.Span[..12], "WEBP").ShouldBeTrue();
    }

    [Fact]
    public void Small_images_are_never_enlarged()
    {
        var variants = Process(TestImages.Jpeg(200, 100));

        variants.Width.ShouldBe(200);
        variants.Height.ShouldBe(100);
        SizeOf(variants.Thumb).ShouldBe((200, 100));
    }

    [Fact]
    public void The_exif_orientation_is_applied_to_the_pixels()
    {
        // Orientation 6: the camera was rotated, so the picture must turn 90 degrees clockwise.
        var variants = Process(TestImages.Jpeg(80, 40, exifOrientation: 6));

        variants.Width.ShouldBe(40);
        variants.Height.ShouldBe(80);
        using var bitmap = SKBitmap.Decode(variants.Medium.ToArray());
        var top = bitmap.GetPixel(20, 10);
        var bottom = bitmap.GetPixel(20, 70);
        top.Red.ShouldBeGreaterThan((byte)200);
        top.Blue.ShouldBeLessThan((byte)60);
        bottom.Blue.ShouldBeGreaterThan((byte)200);
        bottom.Red.ShouldBeLessThan((byte)60);
    }

    [Fact]
    public void A_large_photo_is_decoded_small_and_still_turned_upright()
    {
        // Large enough for the codec to decode it at a fraction of its size, and taken with the phone upright.
        var variants = Process(TestImages.Jpeg(2000, 1000, exifOrientation: 6));

        (variants.Width, variants.Height).ShouldBe((540, 1080));
        SizeOf(variants.Medium).ShouldBe((540, 1080));
        SizeOf(variants.Thumb).ShouldBe((160, 320));
        using var bitmap = SKBitmap.Decode(variants.Medium.ToArray());
        var top = bitmap.GetPixel(270, 200);
        var bottom = bitmap.GetPixel(270, 880);
        top.Red.ShouldBeGreaterThan((byte)200);
        top.Blue.ShouldBeLessThan((byte)60);
        bottom.Blue.ShouldBeGreaterThan((byte)200);
        bottom.Red.ShouldBeLessThan((byte)60);
    }

    [Theory]
    [InlineData("this is not an image")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    public void A_file_that_is_not_an_image_is_rejected(string content)
    {
        var result = _processor.Process(Encoding.UTF8.GetBytes(content), ManyPixels);

        result.Variants.ShouldBeNull();
        result.RejectionCode.ShouldBe(MediaErrors.InvalidImageCode);
    }

    [Fact]
    public void An_image_with_too_many_pixels_is_rejected_before_decoding_it()
    {
        var result = _processor.Process(TestImages.Jpeg(100, 100), maxPixels: 9_999);

        result.Variants.ShouldBeNull();
        result.RejectionCode.ShouldBe(MediaErrors.ImageTooLargeCode);
    }

    private ImageVariants Process(byte[] image)
    {
        var result = _processor.Process(image, ManyPixels);
        result.RejectionCode.ShouldBeNull();
        return result.Variants.ShouldNotBeNull();
    }

    private static (int Width, int Height) SizeOf(ReadOnlyMemory<byte> image)
    {
        using var codec = SKCodec.Create(new MemoryStream(image.ToArray()));
        return (codec.Info.Width, codec.Info.Height);
    }
}
