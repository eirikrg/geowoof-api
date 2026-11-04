using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.Sessions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Users;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Identity.Application.Authentication;

public sealed record SignInWithPasswordCommand(string Email, string Password) : ICommand<SessionDto>;

internal sealed class SignInWithPasswordCommandValidator : AbstractValidator<SignInWithPasswordCommand>
{
    public SignInWithPasswordCommandValidator()
    {
        RuleFor(command => command.Email).NotEmpty();
        RuleFor(command => command.Password).NotEmpty();
    }
}

/// <summary>
/// Email and password sign-in. Unknown emails, accounts without password and wrong passwords all
/// return the same error (and take the same time), so accounts cannot be enumerated.
/// </summary>
internal sealed class SignInWithPasswordCommandHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    SessionService sessions,
    IIdentityUnitOfWork unitOfWork,
    IOptions<AuthSettings> settings,
    TimeProvider timeProvider) : ICommandHandler<SignInWithPasswordCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(SignInWithPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = Email.IsValid(command.Email)
            ? await users.GetByEmailAsync(Email.Create(command.Email), cancellationToken)
            : null;
        if (user?.PasswordHash is not { } passwordHash)
        {
            passwordHasher.SimulateVerification(command.Password);
            return IdentityErrors.InvalidCredentials;
        }

        var now = timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
        {
            return IdentityErrors.LockedOut(user.LockoutEndsAt!.Value - now);
        }

        var check = passwordHasher.Verify(passwordHash, command.Password);
        if (check == PasswordCheck.Failed)
        {
            user.RecordFailedSignIn(now, settings.Value.Lockout);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return IdentityErrors.InvalidCredentials;
        }

        if (check == PasswordCheck.SucceededNeedsRehash)
        {
            user.SetPasswordHash(passwordHasher.Hash(command.Password));
        }

        user.RecordSuccessfulSignIn();
        var session = sessions.Start(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return session;
    }
}
