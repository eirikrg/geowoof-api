using System.Diagnostics;

namespace GeoWoof.BuildingBlocks.Application;

/// <summary>OpenTelemetry sources for the application layer (the host registers <c>GeoWoof.*</c>).</summary>
public static class ApplicationDiagnostics
{
    public const string ActivitySourceName = "GeoWoof.Application";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
