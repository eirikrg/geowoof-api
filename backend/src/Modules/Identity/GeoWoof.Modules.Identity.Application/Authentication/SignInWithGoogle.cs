using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.Sessions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application.Authentication;

public sealed record SignInWithGoogleCommand(string IdToken, string? Language) : ICommand<SessionDto>;

internal sealed class SignInWithGoogleCommandValidator : AbstractValidator<SignInWithGoogleCommand>
{
    public SignInWithGoogleCommandValidator()
    {
        RuleFor(command => command.IdToken).NotEmpty();
        RuleFor(command => command.Language).SupportedLanguage();
    }
}

/// <summary>
/// Signs in with a Google ID token: finds the account by Google subject, links an existing account
/// with the same email, or creates a new one (ADR-0005).
/// </summary>
internal sealed class SignInWithGoogleCommandHandler(
    IGoogleIdTokenValidator google,
    IUserRepository users,
    SessionService sessions,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<SignInWithGoogleCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(SignInWithGoogleCommand command, CancellationToken cancellationToken)
    {
        if (!google.IsEnabled)
        {
            return IdentityErrors.GoogleDisabled;
        }

        var identity = await google.ValidateAsync(command.IdToken, cancellationToken);
        if (identity is null || !Email.IsValid(identity.Email))
        {
            return IdentityErrors.GoogleTokenInvalid;
        }

        if (!identity.EmailVerified)
        {
            return IdentityErrors.GoogleEmailUnverified;
        }

        var user = await users.GetByExternalLoginAsync(ExternalLogin.Google, identity.Subject, cancellationToken);
        var isNewUser = false;
        if (user is null)
        {
            var email = Email.Create(identity.Email);
            user = await users.GetByEmailAsync(email, cancellationToken);
            if (user is null)
            {
                var now = timeProvider.GetUtcNow();
                user = User.RegisterWithGoogle(
                    Guid.CreateVersion7(now),
                    email,
                    identity.Subject,
                    DisplayNameFor(identity, email),
                    LanguageCodes.Parse(command.Language),
                    now);
                users.Add(user);
                isNewUser = true;
            }
            else if (user.GoogleSubject is not null)
            {
                // The account is already linked to another Google account with the same email.
                return IdentityErrors.GoogleTokenInvalid;
            }
            else if (user.LinkGoogle(identity.Subject))
            {
                // The unverified account may have been created by someone else: end its sessions.
                await sessions.RevokeAllAsync(user.Id, RevocationReason.AccountSecured, cancellationToken);
            }
        }

        var session = sessions.Start(user, isNewUser);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // A concurrent request created or linked the same account; the client can simply retry.
            return IdentityErrors.GoogleTokenInvalid;
        }

        return session;
    }

    private static string DisplayNameFor(GoogleIdentity identity, Email email)
    {
        var name = string.IsNullOrWhiteSpace(identity.Name) ? email.LocalPart : identity.Name.Trim();
        return name.Length <= User.DisplayNameMaxLength ? name : name[..User.DisplayNameMaxLength];
    }
}
