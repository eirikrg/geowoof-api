using System.Text.Json;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace GeoWoof.Modules.Walks.Infrastructure.Persistence;

/// <summary>Schema <c>walks</c>: finished walks with their routes.</summary>
public sealed class WalksDbContext(DbContextOptions<WalksDbContext> options) : ModuleDbContext(options), IWalksUnitOfWork
{
    public const string SchemaName = "walks";

    public override string Schema => SchemaName;

    public DbSet<Walk> Walks => Set<Walk>();

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.ApplyConfiguration(new WalkConfiguration());
    }
}

internal sealed class WalkConfiguration : IEntityTypeConfiguration<Walk>
{
    public const string RouteProperty = "Route";
    public const string StartPointProperty = "StartPoint";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Walk> builder)
    {
        builder.ToTable("walks");
        builder.HasKey(walk => walk.Id);
        builder.Property(walk => walk.Id).ValueGeneratedNever();
        builder.Property(walk => walk.TimeZone).HasMaxLength(64).IsRequired();
        builder.Property(walk => walk.Visibility).HasConversion<string>().HasMaxLength(16);
        builder.Property(walk => walk.Note).HasMaxLength(Walk.NoteMaxLength);
        builder.Ignore(walk => walk.PhotoAssetIds);

        builder.Property(walk => walk.Segments)
            .HasColumnName("track")
            .HasColumnType("jsonb")
            .HasConversion(
                segments => JsonSerializer.Serialize(segments, JsonOptions),
                json => JsonSerializer.Deserialize<List<WalkSegment>>(json, JsonOptions) ?? new List<WalkSegment>(),
                new ValueComparer<IReadOnlyList<WalkSegment>>(
                    (first, second) => ReferenceEquals(first, second),
                    segments => segments.Count,
                    segments => segments));
        builder.Property(walk => walk.PreviewPolylines)
            .HasColumnType("text[]")
            .HasConversion(
                previews => previews.ToArray(),
                previews => previews,
                new ValueComparer<IReadOnlyList<string>>(
                    (first, second) => ReferenceEquals(first, second),
                    previews => previews.Count,
                    previews => previews));

        // For spatial queries only: the domain works with the track and never reads these columns.
        builder.Property<MultiLineString>(RouteProperty).HasColumnType("geography (MultiLineString, 4326)").IsRequired();
        builder.Property<Point>(StartPointProperty).HasColumnType("geography (Point, 4326)").IsRequired();

        builder.HasIndex(walk => new { walk.OwnerId, walk.StartedAt, walk.Id });

        builder.OwnsMany(walk => walk.Pets, pets =>
        {
            pets.ToTable("walk_pets");
            pets.WithOwner().HasForeignKey("walk_id");
            pets.HasKey("walk_id", nameof(WalkPet.PetId));
            pets.Property(pet => pet.PetId).ValueGeneratedNever();
            pets.HasIndex(pet => pet.PetId);
        });
        builder.Navigation(walk => walk.Pets).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany<WalkPhoto>("_photos", photos =>
        {
            photos.ToTable("walk_photos");
            photos.WithOwner().HasForeignKey("walk_id");
            photos.HasKey("walk_id", nameof(WalkPhoto.AssetId));
            photos.Property(photo => photo.AssetId).ValueGeneratedNever();
        });
    }
}

/// <summary>
/// Used by <c>dotnet ef migrations</c> only (no database connection is opened):
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Modules/Walks/GeoWoof.Modules.Walks.Infrastructure --startup-project src/Modules/Walks/GeoWoof.Modules.Walks.Infrastructure --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class WalksDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WalksDbContext>
{
    public WalksDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WalksDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geowoof_design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WalksDbContext.SchemaName).UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new WalksDbContext(options);
    }
}

internal sealed class WalkRepository(WalksDbContext dbContext) : IWalkRepository
{
    private const int Wgs84 = 4326;

    private static readonly GeometryFactory Geometry = new(new PrecisionModel(), Wgs84);

    /// <summary>Pets and photos in queries of their own: joined together, each row would multiply the other.</summary>
    private IQueryable<Walk> WalksWithChildren => dbContext.Walks.AsSplitQuery();

    public Task<Walk?> GetByIdAsync(Guid walkId, CancellationToken cancellationToken) =>
        WalksWithChildren.FirstOrDefaultAsync(walk => walk.Id == walkId, cancellationToken);

    public async Task<IReadOnlyList<WalkSummaryRow>> GetPageAsync(Guid ownerId, Guid? petId, IReadOnlyCollection<ContentVisibility>? visibilities, WalkPageCursor? after, int take, CancellationToken cancellationToken)
    {
        var query = WalksWithChildren.AsNoTracking().Where(walk => walk.OwnerId == ownerId);
        if (petId is { } pet)
        {
            query = query.Where(walk => walk.Pets.Any(walkPet => walkPet.PetId == pet));
        }

        if (visibilities is not null)
        {
            query = query.Where(walk => visibilities.Contains(walk.Visibility));
        }

        if (after is not null)
        {
            query = query.Where(walk => walk.StartedAt < after.StartedAt || (walk.StartedAt == after.StartedAt && walk.Id.CompareTo(after.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(walk => walk.StartedAt)
            .ThenByDescending(walk => walk.Id)
            .Take(take)
            .Select(walk => new
            {
                walk.Id,
                walk.OwnerId,
                walk.StartedAt,
                walk.EndedAt,
                walk.TimeZone,
                PetIds = walk.Pets.Select(walkPet => walkPet.PetId).ToList(),
                walk.Visibility,
                walk.Note,
                walk.DistanceM,
                walk.DurationS,
                walk.MovingTimeS,
                walk.AverageSpeedMps,
                walk.PaceSecondsPerKm,
                walk.PreviewPolylines,
                Photos = EF.Property<List<WalkPhoto>>(walk, "_photos").OrderBy(photo => photo.Position).Select(photo => photo.AssetId).ToList(),
                walk.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new WalkSummaryRow(
            row.Id,
            row.OwnerId,
            row.StartedAt,
            row.EndedAt,
            row.TimeZone,
            row.PetIds,
            row.Visibility,
            row.Note,
            row.DistanceM,
            row.DurationS,
            row.MovingTimeS,
            row.AverageSpeedMps,
            row.PaceSecondsPerKm,
            row.PreviewPolylines,
            row.Photos,
            row.UpdatedAt)).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<WalkSegment>>> GetTracksAsync(IReadOnlyCollection<Guid> walkIds, CancellationToken cancellationToken) =>
        await dbContext.Walks.AsNoTracking()
            .Where(walk => walkIds.Contains(walk.Id))
            .Select(walk => new { walk.Id, walk.Segments })
            .ToDictionaryAsync(walk => walk.Id, walk => walk.Segments, cancellationToken);

    public async Task<IReadOnlyList<WalkStatsRow>> GetStatsRowsAsync(Guid ownerId, Guid? petId, IReadOnlyCollection<ContentVisibility>? visibilities, CancellationToken cancellationToken)
    {
        var query = dbContext.Walks.AsNoTracking().Where(walk => walk.OwnerId == ownerId);
        if (petId is { } pet)
        {
            query = query.Where(walk => walk.Pets.Any(walkPet => walkPet.PetId == pet));
        }

        if (visibilities is not null)
        {
            query = query.Where(walk => visibilities.Contains(walk.Visibility));
        }

        return await query
            .Select(walk => new WalkStatsRow(walk.StartedAt, walk.TimeZone, walk.DistanceM, walk.DurationS, walk.MovingTimeS))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Walk>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        await WalksWithChildren.Where(walk => walk.OwnerId == ownerId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Walk>> GetByPetAsync(Guid petId, CancellationToken cancellationToken) =>
        await WalksWithChildren.Where(walk => walk.Pets.Any(pet => pet.PetId == petId)).ToListAsync(cancellationToken);

    public void Add(Walk walk, WalkTrack track)
    {
        var entry = dbContext.Walks.Add(walk);
        var lines = track.Points
            .Where(points => points.Count >= 2)
            .Select(points => Geometry.CreateLineString(points.Select(point => new Coordinate(point.Position.Longitude, point.Position.Latitude)).ToArray()))
            .ToArray();
        entry.Property(WalkConfiguration.RouteProperty).CurrentValue = Geometry.CreateMultiLineString(lines);

        var start = track.Points[0][0].Position;
        entry.Property(WalkConfiguration.StartPointProperty).CurrentValue = Geometry.CreatePoint(new Coordinate(start.Longitude, start.Latitude));
    }

    public void Remove(Walk walk) => dbContext.Walks.Remove(walk);
}
