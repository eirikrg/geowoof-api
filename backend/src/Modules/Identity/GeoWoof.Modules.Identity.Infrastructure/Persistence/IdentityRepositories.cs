using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
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

    public async Task<IReadOnlyList<User>> GetPublicByUsernamesAsync(IReadOnlyCollection<Username> usernames, CancellationToken cancellationToken) =>
        await dbContext.Users.AsNoTracking().Where(user => user.Username != null && usernames.Contains(user.Username)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<User>> GetPublicByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        await dbContext.Users.AsNoTracking().Where(user => userIds.Contains(user.Id) && user.Username != null).ToListAsync(cancellationToken);

    public Task<IReadOnlyList<RelevancePosition>> SearchAsync(string text, IReadOnlyCollection<Guid> excludedIds, IReadOnlyCollection<Guid> followedIds, RelevancePosition after, int take, CancellationToken cancellationToken)
    {
        var pattern = TextSearch.ContainsPattern(text);
        var excluded = excludedIds.ToArray();
        var followed = followedIds.ToArray();
        return TextSearch.RunAsync(
            dbContext,
            $"""
            SELECT r.id, r.score
            FROM (
                SELECT u.id, round(GREATEST(
                    word_similarity(identity.normalize_search_text({text}), identity.normalize_search_text(u.username)),
                    CASE WHEN NOT u.is_private OR u.id = ANY({followed})
                        THEN word_similarity(identity.normalize_search_text({text}), identity.normalize_search_text(u.display_name))
                        ELSE 0 END)::numeric, 4) AS score
                FROM identity.users AS u
                WHERE u.username IS NOT NULL
                    AND NOT (u.id = ANY({excluded}))
                    AND (identity.normalize_search_text({text}) <% identity.normalize_search_text(u.username)
                        OR identity.normalize_search_text(u.username) LIKE identity.normalize_search_text({pattern})
                        OR ((NOT u.is_private OR u.id = ANY({followed}))
                            AND (identity.normalize_search_text({text}) <% identity.normalize_search_text(u.display_name)
                                OR identity.normalize_search_text(u.display_name) LIKE identity.normalize_search_text({pattern}))))
            ) AS r
            WHERE (r.score, r.id) < ({after.Score}, {after.Id})
            ORDER BY r.score DESC, r.id DESC
            LIMIT {take}
            """,
            cancellationToken);
    }

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
