using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Verification;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Identity.Application.EmailVerification;

/// <summary>Issues and redeems one-time email codes. Callers save the unit of work.</summary>
internal sealed class VerificationCodeService(
    IVerificationCodeRepository codes,
    ISecretGenerator secrets,
    IOptions<AuthSettings> settings,
    TimeProvider timeProvider)
{
    private readonly VerificationCodePolicy _policy = settings.Value.VerificationCodes;

    public TimeSpan Lifetime => _policy.Lifetime;

    public async Task<TimeSpan> CooldownRemainingAsync(Guid userId, VerificationPurpose purpose, CancellationToken cancellationToken)
    {
        var latest = await codes.GetLatestAsync(userId, purpose, cancellationToken);
        return latest?.CooldownRemaining(timeProvider.GetUtcNow(), _policy) ?? TimeSpan.Zero;
    }

    /// <summary>Invalidates any usable code for the purpose and returns a new plain code to send.</summary>
    public async Task<string> IssueAsync(Guid userId, VerificationPurpose purpose, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var previous in await codes.GetUsableAsync(userId, purpose, now, cancellationToken))
        {
            previous.Invalidate(now);
        }

        var code = secrets.NewVerificationCode();
        codes.Add(VerificationCode.Issue(userId, purpose, secrets.HashVerificationCode(userId, purpose, code), now, _policy));
        return code;
    }

    /// <summary>Checks the code against the latest one issued. The attempt is recorded even when it fails.</summary>
    public async Task<Result> RedeemAsync(Guid userId, VerificationPurpose purpose, string code, CancellationToken cancellationToken)
    {
        var latest = await codes.GetLatestAsync(userId, purpose, cancellationToken);
        return latest is null
            ? IdentityErrors.CodeInvalid
            : latest.Redeem(secrets.HashVerificationCode(userId, purpose, code), timeProvider.GetUtcNow(), _policy);
    }
}
