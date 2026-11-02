using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace GeoWoof.BuildingBlocks.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "Postgres";

    /// <summary>Registers the shared <see cref="NpgsqlDataSource"/> (one connection pool for every module) and <see cref="DatabaseOptions"/>.</summary>
    public static IServiceCollection AddGeoWoofPostgres(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<DatabaseOptions>()
            .Configure(options =>
            {
                options.ApplyMigrationsOnStartup = environment.IsDevelopment();
                options.SeedDevelopmentData = environment.IsDevelopment();
            })
            .BindConfiguration(DatabaseOptions.SectionName);

        services.AddOptions<ConnectionStringOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.Value = configuration.GetConnectionString(ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Value),
                $"Missing connection string 'ConnectionStrings:{ConnectionStringName}'.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var builder = new NpgsqlDataSourceBuilder(configuration.GetConnectionString(ConnectionStringName));
            builder.UseNetTopologySuite();
            return builder.Build();
        });

        return services;
    }

    /// <summary>
    /// Registers a module DbContext on the shared data source with snake_case naming, the schema's own
    /// migrations history table, the domain events/Outbox interceptor and the schema's Outbox processor.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : ModuleDbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        services.AddDbContext<TContext>((serviceProvider, options) => options
            .UseNpgsql(
                serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                npgsql => npgsql
                    .MigrationsHistoryTable("__ef_migrations_history", schema)
                    .UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(serviceProvider.GetRequiredService<ModuleSaveChangesInterceptor>()));

        services.AddSingleton(new ModuleDbContextRegistration(typeof(TContext), schema));
        services.AddHostedService<OutboxProcessor<TContext>>();
        return services;
    }

    private sealed class ConnectionStringOptions
    {
        public string? Value { get; set; }
    }
}
