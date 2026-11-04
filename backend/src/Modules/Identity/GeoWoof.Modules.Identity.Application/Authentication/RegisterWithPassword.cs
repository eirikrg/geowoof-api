using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.EmailVerification;
using GeoWoof.Modules.Identity.Application.Sessions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;

namespace GeoWoof.Modules.Identity.Application.Authentication;

/// <summary>Creates an account with email and password, starts a session and emails a verification code.</summary>
public sealed record RegisterWithPasswordCommand(string Email, string Password, string DisplayName, string? Language) : ICommand<SessionDto>;

internal sealed class RegisterWithPasswordCommandValidator : AbstractValidator<RegisterWithPasswordCommand>
{
    public RegisterWithPasswordCommandValidator()
    {
        RuleFor(command => command.Email).ValidEmail();
        RuleFor(command => command.Password).ValidPassword();
        RuleFor(command => command.DisplayName).ValidDisplayName();
        RuleFor(command => command.Language).SupportedLanguage();
    }
}

internal sealed class RegisterWithPasswordCommandHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    SessionService sessions,
    VerificationCodeService verificationCodes,
    IdentityEmailDispatcher emails,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RegisterWithPasswordCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(RegisterWithPasswordCommand command, CancellationToken cancellationToken)
    {
        var email = Email.Create(command.Email);
        if (await users.EmailExistsAsync(email, cancellationToken))
        {
            return IdentityErrors.EmailTaken;
        }

        var now = timeProvider.GetUtcNow();
        var user = User.RegisterWithPassword(
            Guid.CreateVersion7(now),
            email,
            passwordHasher.Hash(command.Password),
            command.DisplayName,
            LanguageCodes.Parse(command.Language),
            now);
        users.Add(user);

        var code = await verificationCodes.IssueAsync(user.Id, VerificationPurpose.EmailVerification, cancellationToken);
        var session = sessions.Start(user, isNewUser: true);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // Another request registered the same email at the same time.
            return IdentityErrors.EmailTaken;
        }

        // The account exists even if the email fails: the user can ask for a new code.
        await emails.TrySendAsync(IdentityEmails.EmailVerification(user, code, verificationCodes.Lifetime), cancellationToken);
        return session;
    }
}
