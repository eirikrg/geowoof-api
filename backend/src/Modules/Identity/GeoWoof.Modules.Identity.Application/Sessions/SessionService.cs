using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Users;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Identity.Application.Sessions;

/// <summary>Starts, rotates and revokes sessions (refresh token families). Callers save the unit of work.</summary>
internal sealed class SessionService(
    IRefreshTokenRepository refreshTokens,
    ISecretGenerator secrets,
    IAccessTokenIssuer accessTokens,
    IOptions<AuthSettings> settings,
    TimeProvider timeProvider)
{
    private readonly AuthSettings _settings = settings.Value;

    /// <summary>A new session (new token family) for a device.</summary>
    public SessionDto Start(User user, bool isNewUser = false)
    {
        var now = timeProvider.GetUtcNow();
        var rawToken = secrets.NewRefreshToken();
        var token = RefreshToken.StartFamily(
            user.Id,
            secrets.HashRefreshToken(rawToken),
            now,
            _settings.RefreshTokenLifetime,
            _settings.RefreshTokenFamilyLifetime);
        refreshTokens.Add(token);
        return Build(user, rawToken, token, now, isNewUser);
    }

    /// <summary>Normal refresh: the presented token is exchanged for its successor.</summary>
    public SessionDto Rotate(RefreshToken current, User user)
    {
        var now = timeProvider.GetUtcNow();
        var rawToken = secrets.NewRefreshToken();
        var successor = current.Successor(secrets.HashRefreshToken(rawToken), now, _settings.RefreshTokenLifetime);
        current.MarkRotated(successor, now);
        refreshTokens.Add(successor);
        return Build(user, rawToken, successor, now, isNewUser: false);
    }

    /// <summary>
    /// Grace-period refresh of a token rotated moments ago (the client lost the response): the
    /// successor issued then is revoked and a fresh one is issued in the same family.
    /// </summary>
    public async Task<SessionDto> ReplaceRecentlyRotatedAsync(RefreshToken presented, User user, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var active in await refreshTokens.GetActiveByFamilyAsync(presented.FamilyId, now, cancellationToken))
        {
            active.Revoke(now, RevocationReason.Superseded);
        }

        var rawToken = secrets.NewRefreshToken();
        var successor = presented.Successor(secrets.HashRefreshToken(rawToken), now, _settings.RefreshTokenLifetime);
        refreshTokens.Add(successor);
        return Build(user, rawToken, successor, now, isNewUser: false);
    }

    public async Task RevokeFamilyAsync(Guid familyId, RevocationReason reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var token in await refreshTokens.GetActiveByFamilyAsync(familyId, now, cancellationToken))
        {
            token.Revoke(now, reason);
        }
    }

    public async Task RevokeAllAsync(Guid userId, RevocationReason reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var token in await refreshTokens.GetActiveByUserAsync(userId, now, cancellationToken))
        {
            token.Revoke(now, reason);
        }
    }

    /// <summary>A new access token for the same session (for example, after the claims changed).</summary>
    public AccessTokenDto IssueAccessToken(User user)
    {
        var accessToken = accessTokens.Issue(user, timeProvider.GetUtcNow());
        return new AccessTokenDto(accessToken.Value, accessToken.ExpiresAt, UserDto.From(user));
    }

    private SessionDto Build(User user, string rawRefreshToken, RefreshToken refreshToken, DateTimeOffset now, bool isNewUser)
    {
        var accessToken = accessTokens.Issue(user, now);
        return new SessionDto(accessToken.Value, accessToken.ExpiresAt, rawRefreshToken, refreshToken.ExpiresAt, isNewUser, UserDto.From(user));
    }
}
