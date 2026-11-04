using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace GeoWoof.TestSupport;

/// <summary>Real JPEG files with real EXIF metadata, built byte by byte, as a phone camera would write them.</summary>
internal static class TestImages
{
    /// <summary>Text hidden in the EXIF block, standing in for GPS coordinates. It must never reach a variant.</summary>
    public const string SecretMarker = "SECRET-GPS-40.4168N-3.7038W";

    private const int ImageDescriptionTag = 0x010E;
    private const int OrientationTag = 0x0112;

    /// <summary>A picture whose left half is red and whose right half is blue.</summary>
    public static byte[] Jpeg(int width, int height, int? exifOrientation = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };
        canvas.DrawRect(0, 0, width / 2f, height, red);
        canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = data.ToArray();
        return exifOrientation is { } orientation ? WithExif(jpeg, orientation) : jpeg;
    }

    public static bool Contains(ReadOnlySpan<byte> bytes, string text) => bytes.IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    /// <summary>Inserts an APP1 segment with a TIFF block right after the JPEG start-of-image marker.</summary>
    private static byte[] WithExif(byte[] jpeg, int orientation)
    {
        var description = Encoding.ASCII.GetBytes(SecretMarker + "\0");
        const int EntryCount = 2;
        const int DataOffset = 8 + 2 + (EntryCount * 12) + 4;

        var tiff = new byte[DataOffset + description.Length];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), EntryCount);

        // TIFF requires its entries sorted by tag.
        WriteEntry(tiff.AsSpan(10), ImageDescriptionTag, type: 2, (uint)description.Length, DataOffset);
        WriteEntry(tiff.AsSpan(22), OrientationTag, type: 3, count: 1, (uint)orientation);
        description.CopyTo(tiff.AsSpan(DataOffset));

        var header = "Exif\0\0"u8;
        var segmentLength = 2 + header.Length + tiff.Length;
        var result = new byte[jpeg.Length + 2 + segmentLength];
        jpeg.AsSpan(0, 2).CopyTo(result);
        result[2] = 0xFF;
        result[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)segmentLength);
        header.CopyTo(result.AsSpan(6));
        tiff.CopyTo(result.AsSpan(6 + header.Length));
        jpeg.AsSpan(2).CopyTo(result.AsSpan(6 + header.Length + tiff.Length));
        return result;
    }

    private static void WriteEntry(Span<byte> entry, int tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], value);
    }
}
