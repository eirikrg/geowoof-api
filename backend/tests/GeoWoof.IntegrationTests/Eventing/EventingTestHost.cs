using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Outbox;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GeoWoof.IntegrationTests.Eventing;

/// <summary>A host with only the shared eventing infrastructure and the two test modules.</summary>
internal sealed class EventingTestHost : IAsyncDisposable
{
    public const int MaxAttempts = 3;

    private readonly IHost _host;

    private EventingTestHost(IHost host)
    {
        _host = host;
    }

    public ConsumerBehavior Behavior => _host.Services.GetRequiredService<ConsumerBehavior>();

    /// <summary>Builds the host, creates the module tables (unless already there) and starts the outbox processors.</summary>
    public static async Task<EventingTestHost> StartAsync(string connectionString, bool createSchema = true)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Outbox:PollingInterval"] = "00:00:00.200",
            ["Outbox:BaseRetryDelay"] = "00:00:00.100",
            ["Outbox:MaxRetryDelay"] = "00:00:00.400",
            ["Outbox:MaxAttempts"] = MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        var services = builder.Services;
        services.AddLogging();
        services.AddGeoWoofPostgres(builder.Environment);
        services.AddGeoWoofEventing();
        services.AddSingleton<ConsumerBehavior>();
        services.AddApplicationHandlers(typeof(KennelsDbContext).Assembly);
        services.AddIntegrationEvents(typeof(KennelOpenedIntegrationEvent).Assembly);
        services.AddModuleDbContext<KennelsDbContext>(KennelsDbContext.SchemaName);
        services.AddModuleDbContext<AuditDbContext>(AuditDbContext.SchemaName);
        services.AddIntegrationEventConsumers<AuditDbContext>(typeof(AuditDbContext).Assembly);
        services.DecorateApplicationHandlers();

        var host = builder.Build();
        if (createSchema)
        {
            await CreateTablesAsync<KennelsDbContext>(host.Services);
            await CreateTablesAsync<AuditDbContext>(host.Services);
        }

        await host.StartAsync(TestContext.Current.CancellationToken);
        return new EventingTestHost(host);
    }

    public async Task<Result<Guid>> OpenKennelAsync(string name)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<OpenKennelCommand, Guid>>();
        return await handler.Handle(new OpenKennelCommand(name), TestContext.Current.CancellationToken);
    }

    public async Task<List<OutboxMessage>> OutboxAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<KennelsDbContext>().OutboxMessages
            .AsNoTracking()
            .OrderBy(message => message.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    public async Task<List<AuditEntry>> AuditAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Entries
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    public async Task<int> InboxCountAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().InboxMessages
            .CountAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }

    private static async Task CreateTablesAsync<TContext>(IServiceProvider services)
        where TContext : ModuleDbContext
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(TestContext.Current.CancellationToken);
    }
}
