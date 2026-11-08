using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Security;

namespace GeoWoof.BuildingBlocks.Presentation.Security;

/// <summary>Authorization policy names shared by every module.</summary>
public static class AuthorizationPolicies
{
    /// <summary>Authenticated user whose email address is verified (outgoing social actions).</summary>
    public const string VerifiedEmail = "verified-email";
}

/// <summary>Rate limiting policy names (the host configures the limits).</summary>
public static class RateLimitPolicies
{
    /// <summary>Credential endpoints (register, login, password reset...), per IP address.</summary>
    public const string Authentication = "auth";

    /// <summary>Session refresh, per IP address.</summary>
    public const string TokenRefresh = "auth-refresh";

    /// <summary>What reaches another person (friend requests, reports), per user.</summary>
    public const string SocialWrites = "social-writes";

    /// <summary>Messages sent, per user.</summary>
    public const string ChatMessages = "chat-messages";
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Id of the authenticated user. Only call it from endpoints that require authorization: a
    /// missing or malformed <c>sub</c> claim there means the authentication setup is broken.
    /// </summary>
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subject = principal.FindFirstValue(GeoWoofClaimTypes.Subject);
        return Guid.TryParse(subject, out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated principal has no valid 'sub' claim.");
    }
}
