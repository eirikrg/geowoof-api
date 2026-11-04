using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace GeoWoof.Modules.Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDbContext dbContext) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> GetByExternalLoginAsync(string provider, string subject, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(
            user => user.ExternalLogins.Any(login => login.Provider == provider && login.Subject == subject),
            cancellationToken);

    public Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken) =>
        dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken);

    public Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken) =>
        dbContext.Users.AnyAsync(user => user.Username == username, cancellationToken);

    public void Add(User user) => dbContext.Users.Add(user);

    public void Remove(User user) => dbContext.Users.Remove(user);
}

internal sealed class RefreshTokenRepository(IdentityDbContext dbContext) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAt == null && token.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public void Add(RefreshToken token) => dbContext.RefreshTokens.Add(token);

    public async Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens.RemoveRange(
            await dbContext.RefreshTokens.Where(token => token.UserId == userId).ToListAsync(cancellationToken));
}

internal sealed class VerificationCodeRepository(IdentityDbContext dbContext) : IVerificationCodeRepository
{
    public Task<VerificationCode?> GetLatestAsync(Guid userId, VerificationPurpose purpose, CancellationToken cancellationToken) =>
        dbContext.VerificationCodes
            .Where(code => code.UserId == userId && code.Purpose == purpose)
            .OrderByDescending(code => code.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<VerificationCode>> GetUsableAsync(Guid userId, VerificationPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.VerificationCodes
            .Where(code => code.UserId == userId && code.Purpose == purpose && code.ConsumedAt == null && code.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public void Add(VerificationCode code) => dbContext.VerificationCodes.Add(code);

    public async Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.VerificationCodes.RemoveRange(
            await dbContext.VerificationCodes.Where(code => code.UserId == userId).ToListAsync(cancellationToken));
}
