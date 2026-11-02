using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoWoof.BuildingBlocks.Infrastructure.Persistence;

/// <summary>A module DbContext registered with <see cref="PersistenceServiceCollectionExtensions.AddModuleDbContext{TContext}"/>.</summary>
public sealed record ModuleDbContextRegistration(Type ContextType, string Schema);

public static partial class DatabaseInitializer
{
    /// <summary>Applies migrations and loads development data according to <see cref="DatabaseOptions"/>.</summary>
    public static async Task InitializeDatabasesAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var options = host.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.ApplyMigrationsOnStartup && !options.SeedDevelopmentData)
        {
            return;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));

        if (options.ApplyMigrationsOnStartup)
        {
            foreach (var registration in services.GetServices<ModuleDbContextRegistration>())
            {
                var context = (ModuleDbContext)services.GetRequiredService(registration.ContextType);
                if (!string.Equals(context.Schema, registration.Schema, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{registration.ContextType.Name} declares schema '{context.Schema}' but was registered with '{registration.Schema}'.");
                }

                LogMigrating(logger, registration.Schema);
                await context.Database.MigrateAsync(cancellationToken);
            }
        }

        if (options.SeedDevelopmentData)
        {
            if (!host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment())
            {
                LogSeedingSkipped(logger);
                return;
            }

            foreach (var seeder in services.GetServices<IDevelopmentDataSeeder>().OrderBy(seeder => seeder.Order))
            {
                LogSeeding(logger, seeder.GetType().Name);
                await seeder.SeedAsync(cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying migrations for schema {Schema}")]
    private static partial void LogMigrating(ILogger logger, string schema);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running development data seeder {Seeder}")]
    private static partial void LogSeeding(ILogger logger, string seeder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database:SeedDevelopmentData is ignored outside the Development environment")]
    private static partial void LogSeedingSkipped(ILogger logger);
}
