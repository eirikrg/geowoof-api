using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Verification;

namespace GeoWoof.Modules.Identity.UnitTests.Domain;

public sealed class SessionAndCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly VerificationCodePolicy Codes = new(TimeSpan.FromMinutes(15), MaxAttempts: 3, TimeSpan.FromSeconds(60));

    [Fact]
    public void A_fresh_refresh_token_is_active_until_it_expires()
    {
        var token = RefreshToken.StartFamily(Guid.NewGuid(), "hash", Now, TimeSpan.FromDays(30), TimeSpan.FromDays(180));

        token.Evaluate(Now.AddDays(29), Grace).ShouldBe(RefreshTokenState.Active);
        token.Evaluate(Now.AddDays(30), Grace).ShouldBe(RefreshTokenState.Expired);
    }

    [Fact]
    public void A_rotated_token_is_tolerated_briefly_and_then_counts_as_reuse()
    {
        var token = RefreshToken.StartFamily(Guid.NewGuid(), "hash-1", Now, TimeSpan.FromDays(30), TimeSpan.FromDays(180));
        var successor = token.Successor("hash-2", Now, TimeSpan.FromDays(30));

        token.MarkRotated(successor, Now);

        token.ReplacedById.ShouldBe(successor.Id);
        token.Evaluate(Now.AddSeconds(30), Grace).ShouldBe(RefreshTokenState.RecentlyRotated);
        token.Evaluate(Now.AddSeconds(31), Grace).ShouldBe(RefreshTokenState.Reused);
        successor.FamilyId.ShouldBe(token.FamilyId);
    }

    [Theory]
    [InlineData(RevocationReason.SignedOut)]
    [InlineData(RevocationReason.Superseded)]
    [InlineData(RevocationReason.PasswordChanged)]
    public void Tokens_revoked_for_other_reasons_are_simply_invalid(RevocationReason reason)
    {
        var token = RefreshToken.StartFamily(Guid.NewGuid(), "hash", Now, TimeSpan.FromDays(30), TimeSpan.FromDays(180));

        token.Revoke(Now, reason);

        token.Evaluate(Now.AddSeconds(1), Grace).ShouldBe(RefreshTokenState.Revoked);
    }

    [Fact]
    public void Successors_never_outlive_the_session_family()
    {
        var token = RefreshToken.StartFamily(Guid.NewGuid(), "hash", Now, TimeSpan.FromDays(30), TimeSpan.FromDays(40));

        var successor = token.Successor("hash-2", Now.AddDays(25), TimeSpan.FromDays(30));

        successor.ExpiresAt.ShouldBe(Now.AddDays(40));
        successor.Evaluate(Now.AddDays(40), Grace).ShouldBe(RefreshTokenState.Expired);
    }

    [Fact]
    public void A_correct_code_is_consumed_once()
    {
        var code = VerificationCode.Issue(Guid.NewGuid(), VerificationPurpose.EmailVerification, "good", Now, Codes);

        code.Redeem("good", Now.AddMinutes(1), Codes).IsSuccess.ShouldBeTrue();
        code.Redeem("good", Now.AddMinutes(2), Codes).Error.ShouldBe(IdentityErrors.CodeInvalid);
    }

    [Fact]
    public void Wrong_attempts_are_counted_until_the_code_is_burnt()
    {
        var code = VerificationCode.Issue(Guid.NewGuid(), VerificationPurpose.PasswordReset, "good", Now, Codes);

        code.Redeem("bad", Now, Codes).Error.ShouldBe(IdentityErrors.CodeInvalid);
        code.Redeem("bad", Now, Codes).Error.ShouldBe(IdentityErrors.CodeInvalid);
        code.Redeem("bad", Now, Codes).Error.ShouldBe(IdentityErrors.CodeAttemptsExceeded);
        code.Redeem("good", Now, Codes).Error.ShouldBe(IdentityErrors.CodeAttemptsExceeded);
        code.Attempts.ShouldBe(3);
    }

    [Fact]
    public void Expired_codes_are_rejected()
    {
        var code = VerificationCode.Issue(Guid.NewGuid(), VerificationPurpose.EmailVerification, "good", Now, Codes);

        code.Redeem("good", Now.AddMinutes(15), Codes).Error.ShouldBe(IdentityErrors.CodeExpired);
    }

    [Fact]
    public void A_new_code_can_only_be_requested_after_the_cooldown()
    {
        var code = VerificationCode.Issue(Guid.NewGuid(), VerificationPurpose.EmailVerification, "good", Now, Codes);

        code.CooldownRemaining(Now.AddSeconds(20), Codes).ShouldBe(TimeSpan.FromSeconds(40));
        code.CooldownRemaining(Now.AddSeconds(61), Codes).ShouldBe(TimeSpan.Zero);
    }
}
