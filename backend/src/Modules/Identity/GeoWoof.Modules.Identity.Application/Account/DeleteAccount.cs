using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application.Account;

/// <summary>
/// Deletes the account after re-authentication: the password, or a fresh Google ID token for
/// Google-only accounts. Publishes <c>identity.user_deleted.v1</c> so other modules erase their data.
/// </summary>
public sealed record DeleteAccountCommand(Guid UserId, string? Password, string? GoogleIdToken) : ICommand;

internal sealed class DeleteAccountCommandHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IVerificationCodeRepository verificationCodes,
    IPasswordHasher passwordHasher,
    IGoogleIdTokenValidator google,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<DeleteAccountCommand>
{
    public async Task<Result> Handle(DeleteAccountCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        var reauthenticated = await ReauthenticateAsync(user, command, cancellationToken);
        if (reauthenticated.IsFailure)
        {
            return reauthenticated;
        }

        user.Delete();
        users.Remove(user);
        await refreshTokens.RemoveAllForUserAsync(user.Id, cancellationToken);
        await verificationCodes.RemoveAllForUserAsync(user.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ReauthenticateAsync(User user, DeleteAccountCommand command, CancellationToken cancellationToken)
    {
        if (user.PasswordHash is { } passwordHash)
        {
            if (string.IsNullOrEmpty(command.Password))
            {
                return IdentityErrors.ReauthenticationRequired;
            }

            return passwordHasher.Verify(passwordHash, command.Password) == PasswordCheck.Failed
                ? IdentityErrors.PasswordIncorrect
                : Result.Success();
        }

        if (string.IsNullOrEmpty(command.GoogleIdToken) || !google.IsEnabled)
        {
            return IdentityErrors.ReauthenticationRequired;
        }

        var identity = await google.ValidateAsync(command.GoogleIdToken, cancellationToken);
        return identity is not null && string.Equals(identity.Subject, user.GoogleSubject, StringComparison.Ordinal)
            ? Result.Success()
            : IdentityErrors.GoogleTokenInvalid;
    }
}
