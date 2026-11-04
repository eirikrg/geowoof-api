using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.EmailVerification;
using GeoWoof.Modules.Identity.Application.Sessions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;

namespace GeoWoof.Modules.Identity.Application.Passwords;

/// <summary>
/// Emails a password reset code if the account exists. Always succeeds, so the response never
/// reveals whether an email is registered; requests within the cooldown are ignored silently.
/// </summary>
public sealed record RequestPasswordResetCommand(string Email) : ICommand;

internal sealed class RequestPasswordResetCommandValidator : AbstractValidator<RequestPasswordResetCommand>
{
    public RequestPasswordResetCommandValidator() => RuleFor(command => command.Email).ValidEmail();
}

internal sealed class RequestPasswordResetCommandHandler(
    IUserRepository users,
    VerificationCodeService verificationCodes,
    IdentityEmailDispatcher emails,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<RequestPasswordResetCommand>
{
    public async Task<Result> Handle(RequestPasswordResetCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByEmailAsync(Email.Create(command.Email), cancellationToken);
        if (user is null)
        {
            return Result.Success();
        }

        if (await verificationCodes.CooldownRemainingAsync(user.Id, VerificationPurpose.PasswordReset, cancellationToken) > TimeSpan.Zero)
        {
            return Result.Success();
        }

        var code = await verificationCodes.IssueAsync(user.Id, VerificationPurpose.PasswordReset, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await emails.TrySendAsync(IdentityEmails.PasswordReset(user, code, verificationCodes.Lifetime), cancellationToken);
        return Result.Success();
    }
}

/// <summary>Sets a new password with an emailed code. Ends every session and lifts any lockout.</summary>
public sealed record ResetPasswordCommand(string Email, string Code, string NewPassword) : ICommand;

internal sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(command => command.Email).NotEmpty();
        RuleFor(command => command.Code).ValidCode();
        RuleFor(command => command.NewPassword).ValidPassword();
    }
}

internal sealed class ResetPasswordCommandHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    VerificationCodeService verificationCodes,
    SessionService sessions,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<ResetPasswordCommand>
{
    public async Task<Result> Handle(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = Email.IsValid(command.Email)
            ? await users.GetByEmailAsync(Email.Create(command.Email), cancellationToken)
            : null;
        if (user is null)
        {
            return IdentityErrors.CodeInvalid;
        }

        var redeemed = await verificationCodes.RedeemAsync(user.Id, VerificationPurpose.PasswordReset, command.Code, cancellationToken);
        if (redeemed.IsSuccess)
        {
            user.SetPasswordHash(passwordHasher.Hash(command.NewPassword));
            user.ClearLockout();

            // Receiving the code proves the user owns the address.
            user.MarkEmailVerified();
            await sessions.RevokeAllAsync(user.Id, RevocationReason.PasswordChanged, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return redeemed;
    }
}

/// <summary>
/// Changes (or, for Google-only accounts, sets) the password. Every other session ends; the caller
/// gets a fresh session.
/// </summary>
public sealed record ChangePasswordCommand(Guid UserId, string? CurrentPassword, string NewPassword) : ICommand<SessionDto>;

internal sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator() => RuleFor(command => command.NewPassword).ValidPassword();
}

internal sealed class ChangePasswordCommandHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    SessionService sessions,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<ChangePasswordCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        if (user.PasswordHash is { } currentHash
            && (string.IsNullOrEmpty(command.CurrentPassword) || passwordHasher.Verify(currentHash, command.CurrentPassword) == PasswordCheck.Failed))
        {
            return IdentityErrors.PasswordIncorrect;
        }

        user.SetPasswordHash(passwordHasher.Hash(command.NewPassword));
        await sessions.RevokeAllAsync(user.Id, RevocationReason.PasswordChanged, cancellationToken);
        var session = sessions.Start(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return session;
    }
}
