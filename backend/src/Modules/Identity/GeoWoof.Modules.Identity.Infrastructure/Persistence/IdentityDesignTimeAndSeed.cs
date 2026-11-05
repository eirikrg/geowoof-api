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

    private static readonly (string Email, string Username, string DisplayName, Language Language)[] SampleUsers =
    [
        ("ana@geowoof.dev", "ana", "Ana", Language.Es),
        ("bea@geowoof.dev", "bea", "Bea", Language.Es),
        ("carlos@geowoof.dev", "carlos", "Carlos", Language.En),
        ("jose@geowoof.dev", "jose_angel", "José Ángel", Language.Es),
    ];

    public int Order => 0;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var sample in SampleUsers)
        {
            var email = Email.Create(sample.Email);
            if (await dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken))
            {
                continue;
            }

            var now = timeProvider.GetUtcNow();
            var user = User.RegisterWithPassword(Guid.CreateVersion7(now), email, passwordHasher.Hash(Password), sample.DisplayName, sample.Language, now);
            user.MarkEmailVerified();
            user.ChangeUsername(Username.FromStorage(sample.Username));
            dbContext.Users.Add(user);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
