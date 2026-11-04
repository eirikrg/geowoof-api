using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.UnitTests.Domain;

public sealed class UserIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly LockoutPolicy Lockout = new(MaxFailedAttempts: 3, Duration: TimeSpan.FromMinutes(5));

    [Theory]
    [InlineData("ana", "ana")]
    [InlineData("  @Ana.Paws ", "ana.paws")]
    [InlineData("perro_99", "perro_99")]
    public void Valid_usernames_are_normalized(string raw, string expected)
    {
        var username = Username.Create(raw);

        username.IsSuccess.ShouldBeTrue();
        username.Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("1ana")]
    [InlineData("ana..paws")]
    [InlineData("ana.")]
    [InlineData("ana paws")]
    [InlineData("añoso")]
    [InlineData("this_username_is_way_too_long_x")]
    public void Malformed_usernames_are_rejected(string raw)
    {
        Username.Check(raw).ShouldBe(UsernameCheck.Invalid);
        Username.Create(raw).Error.ShouldBe(IdentityErrors.UsernameInvalid);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("GeoWoof")]
    [InlineData("@support")]
    public void Reserved_usernames_are_rejected(string raw)
    {
        Username.Check(raw).ShouldBe(UsernameCheck.Reserved);
        Username.Create(raw).Error.ShouldBe(IdentityErrors.UsernameReserved);
    }

    [Theory]
    [InlineData(" Ana@Example.COM ", true, "ana@example.com")]
    [InlineData("ana@example", false, null)]
    [InlineData("ana example@x.com", false, null)]
    [InlineData("", false, null)]
    public void Emails_are_validated_and_normalized(string raw, bool valid, string? normalized)
    {
        Email.IsValid(raw).ShouldBe(valid);
        if (valid)
        {
            Email.Create(raw).Value.ShouldBe(normalized);
        }
    }

    [Fact]
    public void Repeated_failed_sign_ins_lock_the_account_for_a_while()
    {
        var user = NewPasswordUser();

        user.RecordFailedSignIn(Now, Lockout);
        user.RecordFailedSignIn(Now, Lockout);
        user.IsLockedOut(Now).ShouldBeFalse();
        user.RecordFailedSignIn(Now, Lockout);

        user.IsLockedOut(Now).ShouldBeTrue();
        user.IsLockedOut(Now.AddMinutes(5)).ShouldBeFalse();
        user.FailedSignInAttempts.ShouldBe(0);
    }

    [Fact]
    public void A_successful_sign_in_resets_the_failure_counter()
    {
        var user = NewPasswordUser();
        user.RecordFailedSignIn(Now, Lockout);
        user.RecordFailedSignIn(Now, Lockout);

        user.RecordSuccessfulSignIn();
        user.RecordFailedSignIn(Now, Lockout);

        user.IsLockedOut(Now).ShouldBeFalse();
    }

    [Fact]
    public void Linking_google_to_an_unverified_account_removes_the_password()
    {
        var user = NewPasswordUser();

        var mustRevokeSessions = user.LinkGoogle("google-subject");

        mustRevokeSessions.ShouldBeTrue();
        user.HasPassword.ShouldBeFalse();
        user.EmailVerified.ShouldBeTrue();
        user.GoogleSubject.ShouldBe("google-subject");
    }

    [Fact]
    public void Linking_google_to_a_verified_account_keeps_the_password()
    {
        var user = NewPasswordUser();
        user.MarkEmailVerified();

        user.LinkGoogle("google-subject").ShouldBeFalse();

        user.HasPassword.ShouldBeTrue();
        user.GoogleSubject.ShouldBe("google-subject");
    }

    [Fact]
    public void Profile_changes_raise_a_domain_event_only_when_something_changes()
    {
        var user = NewPasswordUser();

        user.ChangeUsername(Username.Create("ana").Value);
        user.ChangeUsername(Username.Create("ana").Value);
        user.ChangeDisplayName("  Ana  ");
        user.ChangeDisplayName("Ana López");

        user.DomainEvents.Count.ShouldBe(2);
        user.DomainEvents.Last().ShouldBe(new UserProfileChangedDomainEvent(user.Id, "ana", "Ana López"));
        user.HasCompletedOnboarding.ShouldBeTrue();
    }

    [Fact]
    public void Deleting_raises_the_deleted_event()
    {
        var user = NewPasswordUser();

        user.Delete();

        user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new UserDeletedDomainEvent(user.Id));
    }

    [Fact]
    public void Display_names_must_have_between_1_and_50_characters()
    {
        User.IsValidDisplayName(" ").ShouldBeFalse();
        User.IsValidDisplayName(new string('a', 51)).ShouldBeFalse();
        User.IsValidDisplayName(" Luna ").ShouldBeTrue();
    }

    private static User NewPasswordUser() =>
        User.RegisterWithPassword(Guid.CreateVersion7(), Email.Create("ana@example.com"), "hash", "Ana", Language.Es, Now);
}
