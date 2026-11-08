using System.Text.Json;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.LiveTracking.Application;
using GeoWoof.Modules.Pets.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GeoWoof.Modules.LiveTracking.Infrastructure;

public static class LiveTrackingModule
{
    public static IServiceCollection AddLiveTrackingModule(this IServiceCollection services)
    {
        services.AddLiveTrackingApplication();
        services.AddOptions<LiveTrackingOptions>().BindConfiguration(LiveTrackingOptions.SectionName);
        services.AddSingleton<ILiveStore, RedisLiveStore>();
        services.AddHostedService<LiveSweepService>();
        services.AddHostedService<LiveSimulator>();
        return services;
    }
}

/// <summary>Redis: nothing of this is kept, and a position that stops arriving disappears.</summary>
internal sealed class RedisLiveStore(IConnectionMultiplexer redis, IOptions<LiveTrackingOptions> options, TimeProvider timeProvider) : ILiveStore
{
    private const double KilometersPerDegree = 111.32;

    /// <summary>The data of a walker lasts longer than their position, because the sweep needs it to know who to tell.</summary>
    private static readonly TimeSpan WalkerLifetime = TimeSpan.FromMinutes(2);

    /// <summary>A connection closes when its token expires, so one older than this was left behind by a server that stopped.</summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan CellsLifetime = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _prefix = options.Value.KeyPrefix;

    private IDatabase Database => redis.GetDatabase();

    private RedisKey GeoKey => _prefix + "geo";

    private RedisKey SeenKey => _prefix + "seen";

    private RedisKey SweepKey => _prefix + "sweep";

    public async Task<LiveWalkerState?> GetWalkerAsync(Guid userId, CancellationToken cancellationToken)
    {
        var value = await Database.StringGetAsync(WalkerKey(userId));
        return value.HasValue ? JsonSerializer.Deserialize<LiveWalkerState>(value.ToString(), JsonOptions) : null;
    }

    public async Task SaveWalkerAsync(LiveWalkerState walker, CancellationToken cancellationToken)
    {
        var database = Database;
        await database.StringSetAsync(WalkerKey(walker.UserId), JsonSerializer.Serialize(walker, JsonOptions), WalkerLifetime);
        await database.SortedSetAddAsync(SeenKey, walker.UserId.ToString(), walker.LastSeenAt.ToUnixTimeMilliseconds());
        if (walker is { Latitude: { } latitude, Longitude: { } longitude })
        {
            await database.GeoAddAsync(GeoKey, longitude, latitude, walker.UserId.ToString());
        }
    }

    public async Task RemoveWalkerAsync(Guid userId, CancellationToken cancellationToken)
    {
        var database = Database;
        await database.KeyDeleteAsync(WalkerKey(userId));
        await database.SortedSetRemoveAsync(SeenKey, userId.ToString());
        await database.SortedSetRemoveAsync(GeoKey, userId.ToString());
    }

    public async Task<IReadOnlyList<LiveWalkerState>> FindInAreaAsync(double north, double south, double east, double west, CancellationToken cancellationToken)
    {
        var database = Database;
        var latitude = (north + south) / 2;
        var longitude = (east + west) / 2;
        var height = (north - south) * KilometersPerDegree;

        // At the edge nearest the equator a degree of longitude is widest. What the box brings of more is filtered below.
        var widestLatitude = north < 0 ? north : south > 0 ? south : 0;
        var width = (east - west) * KilometersPerDegree * Math.Cos(widestLatitude * Math.PI / 180);
        var found = await database.GeoSearchAsync(
            GeoKey,
            longitude,
            latitude,
            new GeoSearchBox(Math.Max(height, 0.01), Math.Max(width, 0.01), GeoUnit.Kilometers),
            options: GeoRadiusOptions.None);
        if (found.Length == 0)
        {
            return [];
        }

        var values = await database.StringGetAsync(found.Select(result => WalkerKey(Guid.Parse(result.Member.ToString()))).ToArray());
        return values
            .Where(value => value.HasValue)
            .Select(value => JsonSerializer.Deserialize<LiveWalkerState>(value.ToString(), JsonOptions)!)
            .Where(walker => walker.Latitude >= south && walker.Latitude <= north && walker.Longitude >= west && walker.Longitude <= east)
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetWalkersNotSeenSinceAsync(DateTimeOffset moment, CancellationToken cancellationToken)
    {
        var values = await Database.SortedSetRangeByScoreAsync(SeenKey, double.NegativeInfinity, moment.ToUnixTimeMilliseconds(), Exclude.Stop);
        return values.Select(value => Guid.Parse(value.ToString())).ToList();
    }

    public Task<bool> TryTakeSweepLockAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Database.StringSetAsync(SweepKey, Environment.MachineName, duration, When.NotExists);

    public async Task<long> AddConnectionAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var database = Database;
        var key = ConnectionsKey(userId);
        var now = timeProvider.GetUtcNow();
        await database.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, (now - ConnectionLifetime).ToUnixTimeMilliseconds());
        await database.SortedSetAddAsync(key, connectionId, now.ToUnixTimeMilliseconds());
        await database.KeyExpireAsync(key, ConnectionLifetime);
        return (await database.SortedSetRankAsync(key, connectionId) ?? 0) + 1;
    }

    public Task RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken cancellationToken) =>
        Database.SortedSetRemoveAsync(ConnectionsKey(userId), connectionId);

    public async Task<IReadOnlyList<string>> GetConnectionsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var database = Database;
        var connections = new List<string>();
        foreach (var userId in userIds)
        {
            connections.AddRange((await database.SortedSetRangeByRankAsync(ConnectionsKey(userId))).Select(value => value.ToString()));
        }

        return connections;
    }

    public async Task<IReadOnlyList<string>> ReplaceCellsAsync(Guid userId, string connectionId, IReadOnlyCollection<string> cells, CancellationToken cancellationToken)
    {
        var database = Database;
        var key = ConnectionCellsKey(connectionId);
        var previous = (await database.SetMembersAsync(key)).Select(value => value.ToString()).ToList();
        foreach (var cell in previous.Except(cells, StringComparer.Ordinal))
        {
            await database.HashDeleteAsync(CellListenersKey(cell), connectionId);
        }

        foreach (var cell in cells)
        {
            await database.HashSetAsync(CellListenersKey(cell), connectionId, userId.ToString());
            await database.KeyExpireAsync(CellListenersKey(cell), CellsLifetime);
        }

        await database.KeyDeleteAsync(key);
        if (cells.Count > 0)
        {
            await database.SetAddAsync(key, cells.Select(cell => (RedisValue)cell).ToArray());
            await database.KeyExpireAsync(key, CellsLifetime);
        }

        return previous;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetCellListenersAsync(string cell, CancellationToken cancellationToken)
    {
        var entries = await Database.HashGetAllAsync(CellListenersKey(cell));
        return entries.ToDictionary(entry => entry.Name.ToString(), entry => Guid.Parse(entry.Value.ToString()), StringComparer.Ordinal);
    }

    private RedisKey WalkerKey(Guid userId) => $"{_prefix}walker:{userId}";

    private RedisKey ConnectionsKey(Guid userId) => $"{_prefix}conns:{userId}";

    private RedisKey ConnectionCellsKey(string connectionId) => $"{_prefix}conncells:{connectionId}";

    private RedisKey CellListenersKey(string cell) => $"{_prefix}cellsubs:{cell}";
}

/// <summary>Every few seconds, in one instance at a time, says who has stopped sending.</summary>
internal sealed partial class LiveSweepService(IServiceScopeFactory scopes, IOptions<LiveTrackingOptions> options, ILogger<LiveSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LiveSweep>().RunAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Without Redis there is nobody to sweep, and the next round tries again.
                LogSweepFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sweep of the live positions failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}

/// <summary>
/// The sample accounts walk around a point, through the same commands as any client, so the same rules apply to
/// them. It is for trying the map without a second phone, and it never runs outside Development.
/// </summary>
internal sealed partial class LiveSimulator(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IOptions<LiveTrackingOptions> options,
    TimeProvider timeProvider,
    ILogger<LiveSimulator> logger) : BackgroundService
{
    /// <summary>About five metres between one position and the next, in degrees.</summary>
    private const double Step = 0.00005;

    /// <summary>How far they go from the middle, in degrees: about 700 metres.</summary>
    private const double Range = 0.0065;

    private static readonly string[] SampleEmails = ["ana@geowoof.dev", "bea@geowoof.dev", "carlos@geowoof.dev", "jose@geowoof.dev", "dani@geowoof.dev"];

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

#pragma warning disable CA5394 // These are not secrets: only where the sample walkers go next.
    private readonly Random _random = new(42);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var simulator = options.Value.Simulator;
        if (!simulator.Enabled || !environment.IsDevelopment())
        {
            return;
        }

        LogSimulatorRunning(logger);
        var walkers = new Dictionary<Guid, (double Latitude, double Longitude, double Heading, Guid WalkId)>();
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await StepAsync(scope.ServiceProvider, walkers, simulator, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Sample data must never get in the way of the API: the next round tries again.
                LogSimulatorFailed(logger, exception);
            }
        }
    }

    private async Task StepAsync(
        IServiceProvider services,
        Dictionary<Guid, (double Latitude, double Longitude, double Heading, Guid WalkId)> walkers,
        LiveSimulatorOptions simulator,
        CancellationToken cancellationToken)
    {
        var identity = services.GetRequiredService<IIdentityApi>();
        var pets = services.GetRequiredService<IPetsApi>();
        var start = services.GetRequiredService<ICommandHandler<StartSharingCommand, StartSharingDto>>();
        var update = services.GetRequiredService<ICommandHandler<UpdatePositionCommand>>();
        foreach (var email in SampleEmails)
        {
            if (await identity.FindUserIdByEmailAsync(email, cancellationToken) is not { } userId)
            {
                continue;
            }

            if (!walkers.TryGetValue(userId, out var walker))
            {
                var petIds = await pets.GetPetIdsAsync(userId, cancellationToken);
                if (petIds.Count == 0)
                {
                    continue;
                }

                walker = (
                    simulator.Latitude + (Range * ((_random.NextDouble() * 2) - 1)),
                    simulator.Longitude + (Range * ((_random.NextDouble() * 2) - 1)),
                    _random.NextDouble() * 2 * Math.PI,
                    Guid.CreateVersion7());
                await start.Handle(new StartSharingCommand(userId, walker.WalkId, petIds.Take(2).ToList()), cancellationToken);
            }

            var heading = walker.Heading + ((_random.NextDouble() - 0.5) * 0.6);
            var latitude = Math.Clamp(walker.Latitude + (Step * Math.Cos(heading)), simulator.Latitude - Range, simulator.Latitude + Range);
            var longitude = Math.Clamp(walker.Longitude + (Step * Math.Sin(heading)), simulator.Longitude - Range, simulator.Longitude + Range);
            walkers[userId] = (latitude, longitude, heading, walker.WalkId);

            var result = await update.Handle(new UpdatePositionCommand(userId, latitude, longitude, 10, timeProvider.GetUtcNow()), cancellationToken);
            if (result.IsFailure)
            {
                // Their session was swept or they do not share: it is asked again in the next round.
                walkers.Remove(userId);
            }
        }
    }
#pragma warning restore CA5394

    [LoggerMessage(Level = LogLevel.Information, Message = "The sample walkers are walking")]
    private static partial void LogSimulatorRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sample walkers could not move")]
    private static partial void LogSimulatorFailed(ILogger logger, Exception exception);
}
