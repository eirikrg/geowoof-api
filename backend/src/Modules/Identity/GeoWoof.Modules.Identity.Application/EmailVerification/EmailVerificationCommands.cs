using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.Sessions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Verification;

namespace GeoWoof.Modules.Identity.Application.EmailVerification;

/// <summary>Emails a new verification code (respecting the resend cooldown).</summary>
public sealed record ResendEmailVerificationCommand(Guid UserId) : ICommand;

internal sealed class ResendEmailVerificationCommandHandler(
    IUserRepository users,
    VerificationCodeService verificationCodes,
    IdentityEmailDispatcher emails,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<ResendEmailVerificationCommand>
{
    public async Task<Result> Handle(ResendEmailVerificationCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        if (user.EmailVerified)
        {
            return IdentityErrors.EmailAlreadyVerified;
        }

        var cooldown = await verificationCodes.CooldownRemainingAsync(user.Id, VerificationPurpose.EmailVerification, cancellationToken);
        if (cooldown > TimeSpan.Zero)
        {
            return IdentityErrors.CodeCooldown(cooldown);
        }

        var code = await verificationCodes.IssueAsync(user.Id, VerificationPurpose.EmailVerification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var sent = await emails.TrySendAsync(IdentityEmails.EmailVerification(user, code, verificationCodes.Lifetime), cancellationToken);
        return sent ? Result.Success() : IdentityErrors.EmailDeliveryFailed;
    }
}

/// <summary>Verifies the email with the code; returns a new access token with <c>email_verified = true</c>.</summary>
public sealed record VerifyEmailCommand(Guid UserId, string Code) : ICommand<AccessTokenDto>;

internal sealed class VerifyEmailCommandValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailCommandValidator() => RuleFor(command => command.Code).ValidCode();
}

internal sealed class VerifyEmailCommandHandler(
    IUserRepository users,
    VerificationCodeService verificationCodes,
    SessionService sessions,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<VerifyEmailCommand, AccessTokenDto>
{
    public async Task<Result<AccessTokenDto>> Handle(VerifyEmailCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        if (user.EmailVerified)
        {
            return IdentityErrors.EmailAlreadyVerified;
        }

        var redeemed = await verificationCodes.RedeemAsync(user.Id, VerificationPurpose.EmailVerification, command.Code, cancellationToken);
        if (redeemed.IsSuccess)
        {
            user.MarkEmailVerified();
        }

        // Saved in both cases: failed attempts count towards the limit.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return redeemed.IsSuccess ? sessions.IssueAccessToken(user) : redeemed.Error;
    }
}
