using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.Modules.Identity.Application;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Application.Authentication;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Sessions;

namespace GeoWoof.Modules.Identity.UnitTests.Application;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task Registering_creates_an_unverified_account_with_a_session_and_emails_a_code()
    {
        await using var host = new IdentityTestHost();

        var result = await host.SendAsync<RegisterWithPasswordCommand, SessionDto>(
            new RegisterWithPasswordCommand(" Ana@Example.com ", "Password1", "Ana", "en"));

        var session = result.Value;
        session.IsNewUser.ShouldBeTrue();
        session.User.Email.ShouldBe("ana@example.com");
        session.User.EmailVerified.ShouldBeFalse();
        session.User.Username.ShouldBeNull();
        session.User.Language.ShouldBe("en");
        session.User.SignInMethods.ShouldBe([UserDto.PasswordMethod]);
        session.RefreshToken.ShouldBe("refresh-1");
        host.RefreshTokens.Items.ShouldHaveSingleItem().TokenHash.ShouldBe("h:refresh-1");
        host.Emails.Sent.ShouldHaveSingleItem().Subject.ShouldContain("123456");
    }

    [Fact]
    public async Task Registering_an_existing_email_is_a_conflict()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("ana@example.com");

        var result = await host.SendAsync<RegisterWithPasswordCommand, SessionDto>(
            new RegisterWithPasswordCommand("ANA@example.com", "Password1", "Ana", null));

        result.Error.ShouldBe(IdentityErrors.EmailTaken);
    }

    [Fact]
    public async Task A_concurrent_registration_of_the_same_email_is_a_conflict()
    {
        await using var host = new IdentityTestHost();
        host.UnitOfWork.FailNextSaveWith = new UniqueConstraintViolationException("ix_users_email", "duplicate", new InvalidOperationException());

        var result = await host.SendAsync<RegisterWithPasswordCommand, SessionDto>(
            new RegisterWithPasswordCommand("ana@example.com", "Password1", "Ana", null));

        result.Error.ShouldBe(IdentityErrors.EmailTaken);
        host.Emails.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("short1", "password")]
    [InlineData("onlyletters", "password")]
    [InlineData("12345678", "password")]
    public async Task Weak_passwords_are_rejected(string password, string field)
    {
        await using var host = new IdentityTestHost();

        var result = await host.SendAsync<RegisterWithPasswordCommand, SessionDto>(
            new RegisterWithPasswordCommand("ana@example.com", password, "Ana", null));

        result.Error.Code.ShouldBe("validation.failed");
        result.Error.FieldErrors!.Keys.ShouldContain(field);
    }

    [Fact]
    public async Task Registration_succeeds_even_if_the_email_cannot_be_sent()
    {
        await using var host = new IdentityTestHost();
        host.Emails.Fail = true;

        var result = await host.SendAsync<RegisterWithPasswordCommand, SessionDto>(
            new RegisterWithPasswordCommand("ana@example.com", "Password1", "Ana", null));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Signing_in_with_the_right_password_starts_a_session()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("ana@example.com", "Password1");

        var result = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Password1"));

        result.Value.User.Email.ShouldBe("ana@example.com");
        result.Value.IsNewUser.ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_emails_and_wrong_passwords_get_the_same_error()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("ana@example.com", "Password1");

        var unknown = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("nobody@example.com", "Password1"));
        var wrong = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Wrong1234"));

        unknown.Error.ShouldBe(IdentityErrors.InvalidCredentials);
        wrong.Error.ShouldBe(IdentityErrors.InvalidCredentials);
        host.PasswordHasher.Simulations.ShouldBe(1);
    }

    [Fact]
    public async Task Too_many_wrong_passwords_lock_the_account_until_the_lockout_ends()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("ana@example.com", "Password1");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Wrong1234"));
        }

        var locked = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Password1"));
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var afterLockout = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Password1"));

        locked.Error.Code.ShouldBe("auth.locked_out");
        locked.Error.RetryAfter.ShouldBe(TimeSpan.FromMinutes(5));
        afterLockout.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Google_only_accounts_cannot_sign_in_with_a_password()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("ana@example.com", password: null);

        var result = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Password1"));

        result.Error.ShouldBe(IdentityErrors.InvalidCredentials);
    }

    [Fact]
    public async Task Refreshing_rotates_the_token()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);

        var refreshed = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken));

        refreshed.Value.RefreshToken.ShouldNotBe(session.RefreshToken);
        var original = host.RefreshTokens.Items.Single(token => token.TokenHash == "h:" + session.RefreshToken);
        original.RevocationReason.ShouldBe(RevocationReason.Rotated);
        original.ReplacedById.ShouldBe(host.RefreshTokens.Items.Single(token => token.TokenHash == "h:" + refreshed.Value.RefreshToken).Id);
    }

    [Fact]
    public async Task Presenting_a_rotated_token_after_the_grace_period_revokes_the_whole_session()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);
        var refreshed = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken));
        host.Time.Advance(TimeSpan.FromMinutes(1));

        var replay = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken));
        var legitimate = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(refreshed.Value.RefreshToken));

        replay.Error.ShouldBe(IdentityErrors.RefreshTokenReused);
        legitimate.Error.ShouldBe(IdentityErrors.RefreshTokenInvalid);
        host.RefreshTokens.Items.ShouldAllBe(token => token.RevokedAt != null);
    }

    [Fact]
    public async Task Presenting_a_rotated_token_within_the_grace_period_issues_a_new_one()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);
        var lost = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken));
        host.Time.Advance(TimeSpan.FromSeconds(10));

        var retry = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken));

        retry.IsSuccess.ShouldBeTrue();
        host.RefreshTokens.Items.Single(token => token.TokenHash == "h:" + lost.Value.RefreshToken).RevocationReason
            .ShouldBe(RevocationReason.Superseded);
        var activeTokens = host.RefreshTokens.Items.Where(token => token.IsActive(host.Time.GetUtcNow())).ToList();
        activeTokens.ShouldHaveSingleItem().TokenHash.ShouldBe("h:" + retry.Value.RefreshToken);
    }

    [Fact]
    public async Task A_concurrent_rotation_asks_the_client_to_retry()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);
        host.UnitOfWork.FailNextSaveWith = new ConcurrencyConflictException("changed");

        var result = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken));

        result.Error.ShouldBe(IdentityErrors.RefreshInProgress);
    }

    [Fact]
    public async Task Unknown_or_expired_refresh_tokens_are_rejected()
    {
        await using var host = new IdentityTestHost();
        var session = await RegisterAsync(host);

        var unknown = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand("not-a-token"));
        host.Time.Advance(TimeSpan.FromDays(31));
        var expired = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(session.RefreshToken));

        unknown.Error.ShouldBe(IdentityErrors.RefreshTokenInvalid);
        expired.Error.ShouldBe(IdentityErrors.RefreshTokenExpired);
    }

    [Fact]
    public async Task Signing_out_revokes_only_that_session_unless_all_devices_are_requested()
    {
        await using var host = new IdentityTestHost();
        var phone = await RegisterAsync(host);
        var tablet = await host.SendAsync<SignInWithPasswordCommand, SessionDto>(new SignInWithPasswordCommand("ana@example.com", "Password1"));

        await host.SendAsync(new SignOutCommand(phone.User.Id, phone.RefreshToken, AllDevices: false));
        var tabletStillWorks = await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(tablet.Value.RefreshToken));
        await host.SendAsync(new SignOutCommand(phone.User.Id, RefreshToken: null, AllDevices: true));

        (await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(phone.RefreshToken))).Error.ShouldBe(IdentityErrors.RefreshTokenInvalid);
        tabletStillWorks.IsSuccess.ShouldBeTrue();
        host.RefreshTokens.Items.ShouldAllBe(token => token.RevokedAt != null);
    }

    [Fact]
    public async Task Google_sign_in_is_unavailable_without_configuration()
    {
        await using var host = new IdentityTestHost();
        host.Google.IsEnabled = false;

        var providers = await host.QueryAsync<GetAuthProvidersQuery, AuthProvidersDto>(new GetAuthProvidersQuery());
        var result = await host.SendAsync<SignInWithGoogleCommand, SessionDto>(new SignInWithGoogleCommand("token", null));

        providers.Value.Google.ShouldBeFalse();
        result.Error.ShouldBe(IdentityErrors.GoogleDisabled);
    }

    [Fact]
    public async Task Google_sign_in_creates_a_verified_account_the_first_time()
    {
        await using var host = new IdentityTestHost();
        host.Google.Tokens["token"] = new GoogleIdentity("subject-1", "Bea@Example.com", EmailVerified: true, "Bea García");

        var first = await host.SendAsync<SignInWithGoogleCommand, SessionDto>(new SignInWithGoogleCommand("token", "es"));
        var second = await host.SendAsync<SignInWithGoogleCommand, SessionDto>(new SignInWithGoogleCommand("token", "es"));

        first.Value.IsNewUser.ShouldBeTrue();
        first.Value.User.EmailVerified.ShouldBeTrue();
        first.Value.User.HasPassword.ShouldBeFalse();
        first.Value.User.DisplayName.ShouldBe("Bea García");
        second.Value.IsNewUser.ShouldBeFalse();
        second.Value.User.Id.ShouldBe(first.Value.User.Id);
    }

    [Fact]
    public async Task Google_sign_in_secures_an_unverified_account_with_the_same_email()
    {
        await using var host = new IdentityTestHost();
        var squatter = await RegisterAsync(host);
        host.Google.Tokens["token"] = new GoogleIdentity("subject-1", "ana@example.com", EmailVerified: true, "Ana");

        var result = await host.SendAsync<SignInWithGoogleCommand, SessionDto>(new SignInWithGoogleCommand("token", null));

        result.Value.User.Id.ShouldBe(squatter.User.Id);
        result.Value.User.HasPassword.ShouldBeFalse();
        (await host.SendAsync<RefreshSessionCommand, SessionDto>(new RefreshSessionCommand(squatter.RefreshToken))).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Google_tokens_that_are_invalid_or_unverified_are_rejected()
    {
        await using var host = new IdentityTestHost();
        host.Google.Tokens["unverified"] = new GoogleIdentity("subject-1", "ana@example.com", EmailVerified: false, "Ana");

        var invalid = await host.SendAsync<SignInWithGoogleCommand, SessionDto>(new SignInWithGoogleCommand("forged", null));
        var unverified = await host.SendAsync<SignInWithGoogleCommand, SessionDto>(new SignInWithGoogleCommand("unverified", null));

        invalid.Error.ShouldBe(IdentityErrors.GoogleTokenInvalid);
        unverified.Error.ShouldBe(IdentityErrors.GoogleEmailUnverified);
    }

    [Fact]
    public async Task An_account_linked_to_another_google_account_is_not_taken_over()
    {
        await using var host = new IdentityTestHost();
        host.AddUser("ana@example.com", password: null);
        host.Google.Tokens["other"] = new GoogleIdentity("another-subject", "ana@example.com", EmailVerified: true, "Ana");

        var result = await host.SendAsync<SignInWithGoogleCommand, SessionDto>(new SignInWithGoogleCommand("other", null));

        result.Error.ShouldBe(IdentityErrors.GoogleTokenInvalid);
    }

    private static async Task<SessionDto> RegisterAsync(IdentityTestHost host) =>
        (await host.SendAsync<RegisterWithPasswordCommand, SessionDto>(
            new RegisterWithPasswordCommand("ana@example.com", "Password1", "Ana", null))).Value;
}
