using GeoWoof.Modules.Identity.Application;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.Account;
using GeoWoof.Modules.Identity.Application.Authentication;
using GeoWoof.Modules.Identity.Application.EmailVerification;
using GeoWoof.Modules.Identity.Application.Passwords;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.UnitTests.Application;

public sealed class AccountTests
{
    [Fact]
    public async Task The_emailed_code_verifies_the_address_and_refreshes_the_claims()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);

        var wrong = await host.SendAsync<VerifyEmailCommand, AccessTokenDto>(new VerifyEmailCommand(session.User.Id, "000000"));
        var right = await host.SendAsync<VerifyEmailCommand, AccessTokenDto>(new VerifyEmailCommand(session.User.Id, "123456"));
        var again = await host.SendAsync<VerifyEmailCommand, AccessTokenDto>(new VerifyEmailCommand(session.User.Id, "123456"));

        wrong.Error.ShouldBe(IdentityErrors.CodeInvalid);
        right.Value.User.EmailVerified.ShouldBeTrue();
        right.Value.AccessToken.ShouldEndWith(":verified");
        again.Error.ShouldBe(IdentityErrors.EmailAlreadyVerified);
    }

    [Fact]
    public async Task A_new_verification_code_respects_the_cooldown_and_replaces_the_old_one()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);

        var tooSoon = await host.SendAsync(new ResendEmailVerificationCommand(session.User.Id));
        host.Time.Advance(TimeSpan.FromSeconds(61));
        host.Secrets.NextCode = "654321";
        var resent = await host.SendAsync(new ResendEmailVerificationCommand(session.User.Id));
        var oldCode = await host.SendAsync<VerifyEmailCommand, AccessTokenDto>(new VerifyEmailCommand(session.User.Id, "123456"));
        var newCode = await host.SendAsync<VerifyEmailCommand, AccessTokenDto>(new VerifyEmailCommand(session.User.Id, "654321"));

        tooSoon.Error.Code.ShouldBe("auth.code_cooldown");
        tooSoon.Error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(60));
        resent.IsSuccess.ShouldBeTrue();
        oldCode.Error.ShouldBe(IdentityErrors.CodeInvalid);
        newCode.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Resending_fails_visibly_when_the_email_cannot_be_delivered()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);
        host.Time.Advance(TimeSpan.FromMinutes(2));
        host.Emails.Fail = true;

        var result = await host.SendAsync(new ResendEmailVerificationCommand(session.User.Id));

        result.Error.ShouldBe(IdentityErrors.EmailDeliveryFailed);
    }

    [Fact]
    public async Task Forgot_password_never_reveals_whether_an_account_exists()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("ana@example.com");

        var unknown = await host.SendAsync(new RequestPasswordResetCommand("nobody@example.com"));
        var known = await host.SendAsync(new RequestPasswordResetCommand("ana@example.com"));
        var withinCooldown = await host.SendAsync(new RequestPasswordResetCommand("ana@example.com"));

        unknown.IsSuccess.ShouldBeTrue();
        known.IsSuccess.ShouldBeTrue();
        withinCooldown.IsSuccess.ShouldBeTrue();
        host.Emails.Sent.ShouldHaveSingleItem().To.ShouldBe("ana@example.com");
    }

    [Fact]
    public async Task Resetting_the_password_ends_every_session_and_verifies_the_email()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);
        host.Time.Advance(TimeSpan.FromMinutes(1));
        host.Secrets.NextCode = "777777";
        await host.SendAsync(new RequestPasswordResetCommand("ana@example.com"));

        var reset = await host.SendAsync(new ResetPasswordCommand("ana@example.com", "777777", "NewPassword9"));
        var oldPassword = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Password1"));
        var newPassword = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "NewPassword9"));

        reset.IsSuccess.ShouldBeTrue();
        oldPassword.Error.ShouldBe(IdentityErrors.InvalidCredentials);
        newPassword.Value.User.EmailVerified.ShouldBeTrue();
        (await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken))).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Reset_codes_cannot_be_used_for_unknown_emails_or_other_purposes()
    {
        await using var host = new IdentityTestHost();
        await RegisterAsync(host);

        var unknown = await host.SendAsync(new ResetPasswordCommand("nobody@example.com", "123456", "NewPassword9"));
        var verificationCodeAsReset = await host.SendAsync(new ResetPasswordCommand("ana@example.com", "123456", "NewPassword9"));

        unknown.Error.ShouldBe(IdentityErrors.CodeInvalid);
        verificationCodeAsReset.Error.ShouldBe(IdentityErrors.CodeInvalid);
    }

    [Fact]
    public async Task Changing_the_password_requires_the_current_one_and_restarts_the_session()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);

        var wrong = await host.SendAsync<ChangePasswordCommand, SessionDto>(new ChangePasswordCommand(session.User.Id, "Nope12345", "NewPassword9"));
        var changed = await host.SendAsync<ChangePasswordCommand, SessionDto>(new ChangePasswordCommand(session.User.Id, "Password1", "NewPassword9"));

        wrong.Error.ShouldBe(IdentityErrors.PasswordIncorrect);
        changed.Value.RefreshToken.ShouldNotBe(session.RefreshToken);
        (await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken))).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Google_only_accounts_can_set_a_first_password()
    {
        await using var host = new IdentityTestHost();
        var user = host.AddUser("ana@example.com", password: null);

        var result = await host.SendAsync<ChangePasswordCommand, SessionDto>(new ChangePasswordCommand(user.Id, null, "NewPassword9"));

        result.Value.User.HasPassword.ShouldBeTrue();
        result.Value.User.SignInMethods.ShouldBe([UserDto.PasswordMethod, UserDto.GoogleMethod]);
    }

    [Fact]
    public async Task Choosing_a_username_completes_onboarding_unless_it_is_taken_or_reserved()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("bea@example.com", username: "bea");
        var session = await RegisterAsync(host);

        var taken = await host.SendAsync<ChangeUsernameCommand, UserDto>(new ChangeUsernameCommand(session.User.Id, "@Bea"));
        var reserved = await host.SendAsync<ChangeUsernameCommand, UserDto>(new ChangeUsernameCommand(session.User.Id, "admin"));
        var invalid = await host.SendAsync<ChangeUsernameCommand, UserDto>(new ChangeUsernameCommand(session.User.Id, "1x"));
        var chosen = await host.SendAsync<ChangeUsernameCommand, UserDto>(new ChangeUsernameCommand(session.User.Id, "Ana.Paws"));

        taken.Error.ShouldBe(IdentityErrors.UsernameTaken);
        reserved.Error.ShouldBe(IdentityErrors.UsernameReserved);
        invalid.Error.Code.ShouldBe("validation.failed");
        chosen.Value.Username.ShouldBe("ana.paws");
        host.Users.Items.Single(user => user.Id == session.User.Id).DomainEvents
            .ShouldHaveSingleItem().ShouldBeOfType<UserProfileChangedDomainEvent>();
    }

    [Theory]
    [InlineData("bea", false, UsernameUnavailableReason.Taken)]
    [InlineData("Support", false, UsernameUnavailableReason.Reserved)]
    [InlineData("x", false, UsernameUnavailableReason.Invalid)]
    [InlineData("@Free.Name", true, null)]
    public async Task Username_availability_explains_why_a_name_cannot_be_used(string username, bool available, UsernameUnavailableReason? reason)
    {
        await using var host = new IdentityTestHost();
        host.AddUser("bea@example.com", username: "bea");

        var result = await host.QueryAsync<CheckUsernameAvailabilityQuery, UsernameAvailabilityDto>(new CheckUsernameAvailabilityQuery(username));

        result.Value.Available.ShouldBe(available);
        result.Value.Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task Updating_the_profile_changes_only_the_given_fields()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);

        var result = await host.SendAsync<UpdateProfileCommand, UserDto>(new UpdateProfileCommand(session.User.Id, DisplayName: null, Language: "en"));

        result.Value.DisplayName.ShouldBe("Ana");
        result.Value.Language.ShouldBe("en");
    }

    [Fact]
    public async Task Deleting_the_account_requires_reauthentication_and_erases_its_sessions()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);

        var missing = await host.SendAsync(new DeleteAccountCommand(session.User.Id, Password: null, GoogleIdToken: null));
        var wrong = await host.SendAsync(new DeleteAccountCommand(session.User.Id, "Wrong1234", null));
        var deleted = await host.SendAsync(new DeleteAccountCommand(session.User.Id, "Password1", null));

        missing.Error.ShouldBe(IdentityErrors.ReauthenticationRequired);
        wrong.Error.ShouldBe(IdentityErrors.PasswordIncorrect);
        deleted.IsSuccess.ShouldBeTrue();
        host.Users.Items.ShouldBeEmpty();
        host.RefreshTokens.Items.ShouldBeEmpty();
        host.Codes.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Google_only_accounts_confirm_deletion_with_their_google_account()
    {
        await using var host = new IdentityTestHost();
        var user = host.AddUser("ana@example.com", password: null);
        host.Google.Tokens["mine"] = new GoogleIdentity(user.GoogleSubject!, "ana@example.com", EmailVerified: true, "Ana");
        host.Google.Tokens["theirs"] = new GoogleIdentity("someone-else", "eve@example.com", EmailVerified: true, "Eve");

        var someoneElse = await host.SendAsync(new DeleteAccountCommand(user.Id, null, "theirs"));
        var deleted = await host.SendAsync(new DeleteAccountCommand(user.Id, null, "mine"));

        someoneElse.Error.ShouldBe(IdentityErrors.GoogleTokenInvalid);
        deleted.IsSuccess.ShouldBeTrue();
    }

    private static async Task<SessionDto> RegisterAsync(IdentityTestHost host) =>
        (await host.SendAsync<RegisterWithPasswordCommand, SessionDto>(
            new RegisterWithPasswordCommand("ana@example.com", "Password1", "Ana", null))).Value;
}
