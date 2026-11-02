namespace GeoWoof.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Loads sample data for local development. Seeders run after migrations, ordered by
/// <see cref="Order"/>, only in the Development environment, and must be idempotent.
/// </summary>
public interface IDevelopmentDataSeeder
{
    /// <summary>Lower values run first (for example, users before pets).</summary>
    int Order { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}
