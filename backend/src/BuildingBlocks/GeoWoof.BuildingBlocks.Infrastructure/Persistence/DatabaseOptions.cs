namespace GeoWoof.BuildingBlocks.Infrastructure.Persistence;

/// <summary>Startup database behaviour (configuration section <c>Database</c>). Both default to <c>true</c> only in Development.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Applies pending EF Core migrations of every module when the host starts.</summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    /// <summary>Loads sample data (<see cref="IDevelopmentDataSeeder"/>). Ignored outside Development.</summary>
    public bool SeedDevelopmentData { get; set; }
}
