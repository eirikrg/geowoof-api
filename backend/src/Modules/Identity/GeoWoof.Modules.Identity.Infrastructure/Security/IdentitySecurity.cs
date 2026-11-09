using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GeoWoof.BuildingBlocks.Application.Security;
using GeoWoof.Modules.Identity.Application;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using IPasswordHasher = GeoWoof.Modules.Identity.Application.Abstractions.IPasswordHasher;

namespace GeoWoof.Modules.Identity.Infrastructure.Security;

/// <summary>Access token settings (configuration section <c>Auth:Jwt</c>).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Auth:Jwt";

    [Required]
    public string Issuer { get; set; } = "geowoof";

    [Required]
    public string Audience { get; set; } = "geowoof-app";

    /// <summary>HMAC-SHA256 key, at least 32 characters. Also the root of the one-time code hashing key.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));

    /// <summary>
    /// The key of <c>docker-compose.yml</c> and of the example settings starts like this. The repository is public, so
    /// anyone could sign tokens for any account with it: outside development the API does not start with it.
    /// </summary>
    internal const string PublicDevelopmentKeyPrefix = "geowoof-local-";

    public static bool IsAllowedIn(IHostEnvironment environment, string signingKey)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment()
            || !(signingKey ?? string.Empty).StartsWith(PublicDevelopmentKeyPrefix, StringComparison.Ordinal);
    }
}

/// <summary>Signs short-lived access tokens (HS256) with the claims of the API contract.</summary>
internal sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> jwtOptions, IOptions<AuthSettings> authSettings) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly JwtOptions _jwt = jwtOptions.Value;
    private readonly SigningCredentials _credentials = new(jwtOptions.Value.CreateSigningKey(), SecurityAlgorithms.HmacSha256);
    private readonly TimeSpan _lifetime = authSettings.Value.AccessTokenLifetime;

    public AccessToken Issue(User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        var expiresAt = now + _lifetime;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [GeoWoofClaimTypes.Subject] = user.Id.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            [GeoWoofClaimTypes.EmailVerified] = user.EmailVerified ? "true" : "false",
        };
        if (user.Username is { } username)
        {
            claims[GeoWoofClaimTypes.Username] = username.Value;
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials,
        });
        return new AccessToken(token, expiresAt);
    }
}

/// <summary>PBKDF2 password hashing from ASP.NET Core Identity (versioned format, transparent re-hashing).</summary>
internal sealed class IdentityPasswordHasher : IPasswordHasher
{
    private static readonly HashSubject Subject = new();
    private readonly PasswordHasher<HashSubject> _hasher = new();
    private readonly Lazy<string> _timingEqualizerHash;

    public IdentityPasswordHasher()
    {
        _timingEqualizerHash = new Lazy<string>(() => _hasher.HashPassword(Subject, Guid.NewGuid().ToString()));
    }

    public string Hash(string password) => _hasher.HashPassword(Subject, password);

    public PasswordCheck Verify(string passwordHash, string password) => _hasher.VerifyHashedPassword(Subject, passwordHash, password) switch
    {
        PasswordVerificationResult.Success => PasswordCheck.Succeeded,
        PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SucceededNeedsRehash,
        _ => PasswordCheck.Failed,
    };

    public void SimulateVerification(string password) =>
        _hasher.VerifyHashedPassword(Subject, _timingEqualizerHash.Value, password ?? string.Empty);

    /// <summary>The Identity hasher is generic over a user type it does not use.</summary>
    private sealed class HashSubject;
}

/// <summary>Random refresh tokens and one-time codes, and their hashes.</summary>
internal sealed class SecretGenerator : ISecretGenerator
{
    private const int RefreshTokenBytes = 32;
    private const int CodeUpperBound = 1_000_000;
    private static readonly byte[] CodeKeyInfo = "geowoof/verification-codes/v1"u8.ToArray();

    private readonly byte[] _codeKey;

    public SecretGenerator(IOptions<JwtOptions> jwtOptions)
    {
        _codeKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwtOptions.Value.SigningKey), 32, salt: null, info: CodeKeyInfo);
    }

    public string NewRefreshToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

    public string HashRefreshToken(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    public string NewVerificationCode() =>
        RandomNumberGenerator.GetInt32(0, CodeUpperBound).ToString("D6", CultureInfo.InvariantCulture);

    public string HashVerificationCode(Guid userId, VerificationPurpose purpose, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(_codeKey, Encoding.UTF8.GetBytes($"{purpose}:{userId:N}:{code}")));
}
