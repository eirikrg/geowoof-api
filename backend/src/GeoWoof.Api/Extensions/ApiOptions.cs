using System.ComponentModel.DataAnnotations;

namespace GeoWoof.Api.Extensions;

/// <summary>HTTP API settings (configuration section <c>Api</c>).</summary>
internal sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Serves the OpenAPI document and the Scalar UI. Defaults to <c>true</c> only in Development.</summary>
    public bool ExposeDocumentation { get; set; }

    /// <summary>Browser origins allowed by CORS. The Android app does not need CORS, so it is empty by default.</summary>
    public string[] CorsAllowedOrigins { get; set; } = [];

    /// <summary>Requests allowed per minute and client (user, or IP address when anonymous) across the whole API.</summary>
    [Range(1, 100_000)]
    public int GlobalPermitsPerMinute { get; set; } = 600;

    /// <summary>Credential endpoints (register, login, password reset...) per minute and IP address.</summary>
    [Range(1, 100_000)]
    public int AuthPermitsPerMinute { get; set; } = 10;

    /// <summary>Session refreshes per minute and IP address.</summary>
    [Range(1, 100_000)]
    public int RefreshPermitsPerMinute { get; set; } = 30;

    /// <summary>What reaches another person (friend requests, reports) per minute and user.</summary>
    [Range(1, 100_000)]
    public int SocialWritesPermitsPerMinute { get; set; } = 30;
}
