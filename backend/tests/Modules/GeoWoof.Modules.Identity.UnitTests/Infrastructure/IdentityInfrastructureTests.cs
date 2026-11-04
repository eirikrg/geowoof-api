using GeoWoof.BuildingBlocks.Application.Security;
using GeoWoof.Modules.Identity.Application;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;
using GeoWoof.Modules.Identity.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GeoWoof.Modules.Identity.UnitTests.Infrastructure;

public sealed class IdentityInfrastructureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly JwtOptions Jwt = new() { SigningKey = "unit-test-signing-key-with-at-least-32-chars" };

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var context = new IdentityDbContextDesignTimeFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().ShouldBeFalse("Run `dotnet ef migrations add` for the Identity module.");
    }

    [Fact]
    public async Task Access_tokens_carry_the_documented_claims_and_validate()
    {
        var issuer = new JwtAccessTokenIssuer(Options.Create(Jwt), Options.Create(new AuthSettings()));
        var user = User.RegisterWithPassword(Guid.CreateVersion7(), Email.Create("ana@example.com"), "hash", "Ana", Language.Es, Now);
        user.ChangeUsername(Username.Create("ana").Value);

        var token = issuer.Issue(user, Now);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, new TokenValidationParameters
        {
            ValidIssuer = Jwt.Issuer,
            ValidAudience = Jwt.Audience,
            IssuerSigningKey = Jwt.CreateSigningKey(),
        });

        validation.IsValid.ShouldBeTrue();
        validation.Claims[GeoWoofClaimTypes.Subject].ShouldBe(user.Id.ToString());
        validation.Claims[GeoWoofClaimTypes.EmailVerified].ShouldBe("false");
        validation.Claims[GeoWoofClaimTypes.Username].ShouldBe("ana");
        token.ExpiresAt.ShouldBe(Now.AddMinutes(15));
    }

    [Fact]
    public void Passwords_are_hashed_with_identity_and_verified()
    {
        var hasher = new IdentityPasswordHasher();

        var hash = hasher.Hash("Password1");

        hash.ShouldNotContain("Password1");
        hasher.Verify(hash, "Password1").ShouldBe(PasswordCheck.Succeeded);
        hasher.Verify(hash, "Password2").ShouldBe(PasswordCheck.Failed);
        Should.NotThrow(() => hasher.SimulateVerification("anything"));
    }

    [Fact]
    public void Refresh_tokens_are_long_random_values_stored_as_hashes()
    {
        var secrets = new SecretGenerator(Options.Create(Jwt));

        var first = secrets.NewRefreshToken();
        var second = secrets.NewRefreshToken();

        first.Length.ShouldBe(43);
        first.ShouldNotBe(second);
        secrets.HashRefreshToken(first).Length.ShouldBe(64);
        secrets.HashRefreshToken(first).ShouldBe(secrets.HashRefreshToken(first));
    }

    [Fact]
    public void Verification_codes_have_six_digits_and_their_hash_is_bound_to_user_and_purpose()
    {
        var secrets = new SecretGenerator(Options.Create(Jwt));
        var userId = Guid.NewGuid();

        var codes = Enumerable.Range(0, 50).Select(_ => secrets.NewVerificationCode()).ToList();
        var hash = secrets.HashVerificationCode(userId, VerificationPurpose.EmailVerification, "123456");

        codes.ShouldAllBe(code => code.Length == 6 && code.All(char.IsAsciiDigit));
        hash.ShouldBe(secrets.HashVerificationCode(userId, VerificationPurpose.EmailVerification, "123456"));
        hash.ShouldNotBe(secrets.HashVerificationCode(userId, VerificationPurpose.PasswordReset, "123456"));
        hash.ShouldNotBe(secrets.HashVerificationCode(Guid.NewGuid(), VerificationPurpose.EmailVerification, "123456"));
        hash.ShouldNotBe(new SecretGenerator(Options.Create(new JwtOptions { SigningKey = new string('k', 40) }))
            .HashVerificationCode(userId, VerificationPurpose.EmailVerification, "123456"));
    }
}
