using System.Buffers.Text;
using System.Text;

namespace GeoWoof.BuildingBlocks.Application.Paging;

/// <summary>A page of a list. <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class PageLimit
{
    public const int Default = 20;
    public const int Max = 50;

    public static int Normalize(int? limit) => Math.Clamp(limit ?? Default, 1, Max);
}

/// <summary>Opaque keyset cursors: clients must not interpret them.</summary>
public static class PageCursor
{
    public static string Encode(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    public static bool TryDecode(string cursor, out string value)
    {
        value = string.Empty;
        if (!Base64Url.IsValid(cursor))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        return true;
    }
}
