using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GeoWoof.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef migrations</c> only (no database connection is opened):
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Modules/Identity/GeoWoof.Modules.Identity.Infrastructure --startup-project src/Modules/Identity/GeoWoof.Modules.Identity.Infrastructure --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class IdentityDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geowoof_design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.SchemaName).UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IdentityDbContext(options);
    }
}

/// <summary>Sample accounts for local development (docs/api.md). Idempotent.</summary>
internal sealed class IdentityDevelopmentSeeder(IdentityDbContext dbContext, IPasswordHasher passwordHasher, TimeProvider timeProvider)
    : IDevelopmentDataSeeder
{
    public const string Password = "GeoWoof2026";

    private static readonly (string Email, string Username, string DisplayName, Language Language, bool IsPrivate, LiveVisibility Live)[] SampleUsers =
    [
        ("ana@geowoof.dev", "ana", "Ana", Language.Es, false, LiveVisibility.Everyone),
        ("bea@geowoof.dev", "bea", "Bea", Language.Es, false, LiveVisibility.FriendsOnly),
        ("carlos@geowoof.dev", "carlos", "Carlos", Language.En, false, LiveVisibility.Everyone),
        ("jose@geowoof.dev", "jose_angel", "José Ángel", Language.Es, false, LiveVisibility.Everyone),
        ("dani@geowoof.dev", "dani", "Dani Prieto", Language.Es, true, LiveVisibility.Everyone),
    ];

    public int Order => 0;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var sample in SampleUsers)
        {
            var email = Email.Create(sample.Email);
            var existing = await dbContext.Users.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);
            if (existing is not null)
            {
                // Accounts created before the live map: what someone has chosen is not touched.
                if (existing.LiveVisibility is null)
                {
                    existing.ChangeLiveSettings(sample.Live, hideStartDistanceM: null);
                }

                continue;
            }

            var now = timeProvider.GetUtcNow();
            var user = User.RegisterWithPassword(Guid.CreateVersion7(now), email, passwordHasher.Hash(Password), sample.DisplayName, sample.Language, now);
            user.MarkEmailVerified();
            user.ChangeUsername(Username.FromStorage(sample.Username));
            user.SetPrivate(sample.IsPrivate);
            user.ChangeLiveSettings(sample.Live, hideStartDistanceM: null);
            dbContext.Users.Add(user);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
