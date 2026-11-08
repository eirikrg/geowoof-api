using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Domain;
using GeoWoof.Modules.Walks.Domain.Routes;
using Microsoft.EntityFrameworkCore;

namespace GeoWoof.Modules.Walks.Infrastructure.Persistence;

/// <summary>Sample walks of the sample accounts along real paths of Madrid. Idempotent.</summary>
internal sealed class WalksDevelopmentSeeder(WalksDbContext dbContext, IWalkRepository walks, IIdentityApi identity, IPetsApi pets, TimeProvider timeProvider) : IDevelopmentDataSeeder
{
    private const string TimeZone = "Europe/Madrid";
    private const int StepSeconds = 4;
    private const long StepMs = StepSeconds * 1000;
    private const long PauseMs = 300_000;
    private const int SecondsPerMinute = 60;
    private const double SpeedMps = 1.35;
    private const int DaysBetweenWalks = 6;

    /// <summary>Approximate waypoints around the Retiro park.</summary>
    private static readonly GeoPoint[] Retiro =
    [
        new(40.41530, -3.68830), new(40.41620, -3.68740), new(40.41740, -3.68620), new(40.41830, -3.68480),
        new(40.41800, -3.68300), new(40.41690, -3.68200), new(40.41560, -3.68170), new(40.41420, -3.68230),
        new(40.41320, -3.68340), new(40.41280, -3.68500), new(40.41340, -3.68680), new(40.41440, -3.68790),
    ];

    /// <summary>Approximate waypoints in the Parque del Oeste.</summary>
    private static readonly GeoPoint[] ParqueDelOeste =
    [
        new(40.42700, -3.72000), new(40.42850, -3.72150), new(40.43020, -3.72270), new(40.43160, -3.72200),
        new(40.43210, -3.72020), new(40.43080, -3.71880), new(40.42900, -3.71830), new(40.42760, -3.71890),
    ];

    private static readonly (string Email, GeoPoint[] Route, int Walks, string? Note)[] Samples =
    [
        ("ana@geowoof.dev", Retiro, 6, "Vuelta al Retiro"),
        ("bea@geowoof.dev", Retiro, 3, null),
        ("carlos@geowoof.dev", ParqueDelOeste, 4, "Parque del Oeste con Rocky"),
        ("jose@geowoof.dev", ParqueDelOeste, 3, null),
        ("dani@geowoof.dev", Retiro, 2, null),
    ];

    private static readonly ContentVisibility[] SampleVisibilities = [ContentVisibility.Everyone, ContentVisibility.Friends, ContentVisibility.OnlyMe];

    public int Order => 20;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var sample in Samples)
        {
            var owner = await identity.FindUserIdByEmailAsync(sample.Email, cancellationToken);
            if (owner is not { } ownerId || await dbContext.Walks.AnyAsync(walk => walk.OwnerId == ownerId, cancellationToken))
            {
                continue;
            }

            var petIds = await pets.GetPetIdsAsync(ownerId, cancellationToken);
            if (petIds.Count > 0)
            {
                AddWalks(ownerId, petIds, sample.Route, sample.Walks, sample.Note);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddWalks(Guid ownerId, IReadOnlyList<Guid> petIds, GeoPoint[] route, int count, string? note)
    {
        var now = timeProvider.GetUtcNow();
        for (var i = 0; i < count; i++)
        {
            // Fixed seed: the sample routes are the same on every machine.
            var random = new Random((route.Length * 100) + (count * 10) + i);
            var startedAt = now.AddDays(-(i * DaysBetweenWalks) - 1).AddMinutes(random.Next(0, 240));
            var (segments, duration) = BuildSegments(route, random, minutes: 15 + random.Next(0, 25), withPause: i % 2 == 1);
            var track = WalkTrack.Parse(segments, duration).Value;
            var petsOfWalk = petIds.Take(1 + (i % petIds.Count)).ToList();
            var details = new WalkDetails(SampleVisibilities[i % SampleVisibilities.Length], i == 0 ? note : null, []);
            var endedAt = startedAt + duration;
            walks.Add(Walk.Record(Guid.CreateVersion7(startedAt), ownerId, startedAt, endedAt, TimeZone, petsOfWalk, details, track, endedAt, now), track);
        }
    }

    private static (List<WalkSegment> Segments, TimeSpan Duration) BuildSegments(GeoPoint[] route, Random random, int minutes, bool withPause)
    {
        var path = AlongRoute(route, (minutes * SecondsPerMinute / StepSeconds) + 1, random);
        var half = path.Count / 2;
        var parts = withPause ? [path.Take(half).ToList(), path.Skip(half).ToList()] : new[] { path };
        var segments = new List<WalkSegment>();
        var startMs = 0L;
        var lastMs = 0L;
        foreach (var part in parts)
        {
            var firstMs = startMs;
            var offsets = part.Select((_, index) => firstMs + (index * StepMs)).ToList();
            segments.Add(new WalkSegment(Polyline.Encode(part), offsets));
            lastMs = offsets[^1];
            startMs = lastMs + PauseMs;
        }

        return (segments, TimeSpan.FromMilliseconds(lastMs));
    }

    /// <summary>Points along the route at walking speed, looping around it, with a little noise.</summary>
    private static List<GeoPoint> AlongRoute(GeoPoint[] route, int count, Random random)
    {
        const double NoiseDegrees = 0.00001;
        var points = new List<GeoPoint>(count);
        var index = 0;
        var position = route[0];
        for (var i = 0; i < count; i++)
        {
            points.Add(new GeoPoint(
                position.Latitude + ((random.NextDouble() - 0.5) * NoiseDegrees),
                position.Longitude + ((random.NextDouble() - 0.5) * NoiseDegrees)));
            var remaining = SpeedMps * StepSeconds;
            while (remaining > 0)
            {
                var target = route[(index + 1) % route.Length];
                var distance = GeoMath.DistanceM(position, target);
                if (distance <= remaining)
                {
                    position = target;
                    index = (index + 1) % route.Length;
                    remaining -= distance;
                }
                else
                {
                    var fraction = remaining / distance;
                    position = new GeoPoint(
                        position.Latitude + ((target.Latitude - position.Latitude) * fraction),
                        position.Longitude + ((target.Longitude - position.Longitude) * fraction));
                    remaining = 0;
                }
            }
        }

        return points;
    }
}
