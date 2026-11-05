using System.Globalization;

namespace GeoWoof.BuildingBlocks.Application.Paging;

/// <summary>A search result in a list sorted from the best match to the worst.</summary>
public sealed record RelevancePosition(decimal Score, Guid Id)
{
    /// <summary>Before the first result: scores go from 0 to 1.</summary>
    public static readonly RelevancePosition Start = new(2, Guid.Empty);
}

/// <summary>Opaque cursors for search results.</summary>
public static class RelevanceCursor
{
    private const char Separator = '|';

    public static string Encode(RelevancePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return PageCursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{position.Score}{Separator}{position.Id}"));
    }

    public static RelevancePosition? Decode(string cursor)
    {
        if (!PageCursor.TryDecode(cursor, out var value))
        {
            return null;
        }

        var parts = value.Split(Separator);
        return parts.Length == 2
            && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var score)
            && Guid.TryParse(parts[1], out var id)
                ? new RelevancePosition(score, id)
                : null;
    }
}
