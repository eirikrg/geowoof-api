using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.Sessions;
using GeoWoof.Modules.Identity.Domain.Sessions;

namespace GeoWoof.Modules.Identity.Application.Authentication;

/// <summary>Ends the session of <paramref name="RefreshToken"/>, or every session of the user.</summary>
public sealed record SignOutCommand(Guid UserId, string? RefreshToken, bool AllDevices) : ICommand;

internal sealed class SignOutCommandHandler(
    IRefreshTokenRepository refreshTokens,
    ISecretGenerator secrets,
    SessionService sessions,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<SignOutCommand>
{
    public async Task<Result> Handle(SignOutCommand command, CancellationToken cancellationToken)
    {
        if (command.AllDevices)
        {
            await sessions.RevokeAllAsync(command.UserId, RevocationReason.SignedOut, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(command.RefreshToken))
        {
            var token = await refreshTokens.GetByHashAsync(secrets.HashRefreshToken(command.RefreshToken), cancellationToken);

            // Only the owner can end a session; unknown tokens are ignored (signing out is idempotent).
            if (token is not null && token.UserId == command.UserId)
            {
                await sessions.RevokeFamilyAsync(token.FamilyId, RevocationReason.SignedOut, cancellationToken);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record GetAuthProvidersQuery : IQuery<AuthProvidersDto>;

internal sealed class GetAuthProvidersQueryHandler(IGoogleIdTokenValidator google) : IQueryHandler<GetAuthProvidersQuery, AuthProvidersDto>
{
    public Task<Result<AuthProvidersDto>> Handle(GetAuthProvidersQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new AuthProvidersDto(google.IsEnabled)));
}
