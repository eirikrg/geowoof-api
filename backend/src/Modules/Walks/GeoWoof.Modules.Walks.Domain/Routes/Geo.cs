namespace GeoWoof.Modules.Walks.Domain.Routes;

public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

public static class GeoMath
{
    private const double EarthRadiusM = 6_371_008.8;

    /// <summary>Haversine distance: precise enough for the short steps of a walk.</summary>
    public static double DistanceM(GeoPoint from, GeoPoint to)
    {
        var latFrom = double.DegreesToRadians(from.Latitude);
        var latTo = double.DegreesToRadians(to.Latitude);
        var deltaLat = latTo - latFrom;
        var deltaLon = double.DegreesToRadians(to.Longitude - from.Longitude);
        var a = (Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2))
            + (Math.Cos(latFrom) * Math.Cos(latTo) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2));
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
