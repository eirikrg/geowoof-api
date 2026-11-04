using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.Sessions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Identity.Application.Authentication;

public sealed record RefreshSessionCommand(string RefreshToken) : ICommand<SessionDto>;

internal sealed class RefreshSessionCommandValidator : AbstractValidator<RefreshSessionCommand>
{
    public RefreshSessionCommandValidator() => RuleFor(command => command.RefreshToken).NotEmpty();
}

/// <summary>
/// Rotates a refresh token. A token presented again after its grace period is treated as stolen and
/// its whole family is revoked; within the grace period the client simply gets a fresh token.
/// </summary>
internal sealed partial class RefreshSessionCommandHandler(
    IRefreshTokenRepository refreshTokens,
    IUserRepository users,
    ISecretGenerator secrets,
    SessionService sessions,
    IIdentityUnitOfWork unitOfWork,
    IOptions<AuthSettings> settings,
    TimeProvider timeProvider,
    ILogger<RefreshSessionCommandHandler> logger) : ICommandHandler<RefreshSessionCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(RefreshSessionCommand command, CancellationToken cancellationToken)
    {
        var token = await refreshTokens.GetByHashAsync(secrets.HashRefreshToken(command.RefreshToken), cancellationToken);
        if (token is null)
        {
            return IdentityErrors.RefreshTokenInvalid;
        }

        var state = token.Evaluate(timeProvider.GetUtcNow(), settings.Value.RefreshTokenReuseGracePeriod);
        switch (state)
        {
            case RefreshTokenState.Expired:
                return IdentityErrors.RefreshTokenExpired;
            case RefreshTokenState.Revoked:
                return IdentityErrors.RefreshTokenInvalid;
            case RefreshTokenState.Reused:
                LogReuseDetected(logger, token.UserId, token.FamilyId);
                await sessions.RevokeFamilyAsync(token.FamilyId, RevocationReason.ReuseDetected, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return IdentityErrors.RefreshTokenReused;
            case RefreshTokenState.Active:
            case RefreshTokenState.RecentlyRotated:
                break;
            default:
                throw new InvalidOperationException($"Unexpected refresh token state {state}.");
        }

        var user = await users.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.RefreshTokenInvalid;
        }

        var session = state == RefreshTokenState.Active
            ? sessions.Rotate(token, user)
            : await sessions.ReplaceRecentlyRotatedAsync(token, user, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Another request rotated the same token at the same moment; a retry takes the grace path.
            return IdentityErrors.RefreshInProgress;
        }

        return session;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token reuse detected for user {UserId}; session family {FamilyId} revoked")]
    private static partial void LogReuseDetected(ILogger logger, Guid userId, Guid familyId);
}
