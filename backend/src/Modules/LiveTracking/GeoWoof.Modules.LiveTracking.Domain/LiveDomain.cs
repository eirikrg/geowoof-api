using System.Text;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.LiveTracking.Domain;

/// <summary>Business errors of the live map. The codes are part of the contract.</summary>
public static class LiveErrors
{
    public static readonly Error InvalidViewport = Error.Validation("live.invalid_viewport", "That is not a valid area of the map.");

    public static readonly Error InvalidRequest = Error.Validation("live.invalid_request", "A walk needs an id and between 1 and 5 pets.");

    public static readonly Error InvalidPosition = Error.Validation("live.invalid_position", "That is not a valid position.");

    public static readonly Error SharingDisabled = Error.Forbidden("live.sharing_disabled", "This person does not share their position.");

    public static readonly Error PetNotOwned = Error.Unprocessable("live.pet_not_owned", "Only your own pets can go on your walk.");

    public static readonly Error NotSharing = Error.Conflict("live.not_sharing", "This person is not sharing their position now.");
}

/// <summary>
/// Cells of about 5 km to divide the map (<see href="https://en.wikipedia.org/wiki/Geohash">geohash</see>):
/// whoever looks at an area listens to its cells, so that a position only reaches those near it.
/// </summary>
public static class GeoHash
{
    public const int Precision = 5;

    private const string Alphabet = "0123456789bcdefghjkmnpqrstuvwxyz";
    private const int BitsPerCharacter = 5;
    private const int LatitudeBits = 12;
    private const int LongitudeBits = 13;

    /// <summary>The height of a cell in degrees.</summary>
    private static readonly double LatitudeStep = 180.0 / (1 << LatitudeBits);

    /// <summary>The width of a cell in degrees.</summary>
    private static readonly double LongitudeStep = 360.0 / (1 << LongitudeBits);

    public static string Encode(double latitude, double longitude)
    {
        var (minLatitude, maxLatitude) = (-90.0, 90.0);
        var (minLongitude, maxLongitude) = (-180.0, 180.0);
        var text = new StringBuilder(Precision);
        var value = 0;
        var bits = 0;
        var isLongitude = true;
        while (text.Length < Precision)
        {
            if (isLongitude)
            {
                var middle = (minLongitude + maxLongitude) / 2;
                value <<= 1;
                if (longitude >= middle)
                {
                    value |= 1;
                    minLongitude = middle;
                }
                else
                {
                    maxLongitude = middle;
                }
            }
            else
            {
                var middle = (minLatitude + maxLatitude) / 2;
                value <<= 1;
                if (latitude >= middle)
                {
                    value |= 1;
                    minLatitude = middle;
                }
                else
                {
                    maxLatitude = middle;
                }
            }

            isLongitude = !isLongitude;
            if (++bits == BitsPerCharacter)
            {
                text.Append(Alphabet[value]);
                value = 0;
                bits = 0;
            }
        }

        return text.ToString();
    }

    /// <summary>The cells that cover an area, or <c>null</c> if it would take more than <paramref name="maxCells"/>.</summary>
    /// <param name="north">The north limit, in degrees.</param>
    /// <param name="south">The south limit, in degrees.</param>
    /// <param name="east">The east limit, in degrees.</param>
    /// <param name="west">The west limit, in degrees. An area with it east of the east limit crosses the antimeridian, and takes too many.</param>
    /// <param name="maxCells">The most that can be asked for.</param>
    public static IReadOnlyList<string>? CellsCovering(double north, double south, double east, double west, int maxCells)
    {
        if (east < west)
        {
            return null;
        }

        var firstRow = RowOf(south);
        var lastRow = RowOf(north);
        var firstColumn = ColumnOf(west);
        var lastColumn = ColumnOf(east);
        if ((long)(lastRow - firstRow + 1) * (lastColumn - firstColumn + 1) > maxCells)
        {
            return null;
        }

        var cells = new List<string>();
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                cells.Add(Encode(-90 + ((row + 0.5) * LatitudeStep), -180 + ((column + 0.5) * LongitudeStep)));
            }
        }

        return cells;
    }

    private static int RowOf(double latitude) => Math.Min((int)Math.Floor((latitude + 90) / LatitudeStep), (1 << LatitudeBits) - 1);

    private static int ColumnOf(double longitude) => Math.Min((int)Math.Floor((longitude + 180) / LongitudeStep), (1 << LongitudeBits) - 1);
}
