namespace GeoWoof.BuildingBlocks.Application.Security;

/// <summary>Claims of the GeoWoof access token (JWT). Inbound claim mapping is disabled, so these names are used as-is.</summary>
public static class GeoWoofClaimTypes
{
    /// <summary>User id (UUID).</summary>
    public const string Subject = "sub";

    /// <summary><c>"true"</c> once the email address has been verified.</summary>
    public const string EmailVerified = "email_verified";

    /// <summary>The @username, once onboarding is complete.</summary>
    public const string Username = "username";
}
