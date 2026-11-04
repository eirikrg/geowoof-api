using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.Modules.Media.Infrastructure.Persistence;

/// <summary>Schema <c>media</c>: the uploaded images.</summary>
public sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : ModuleDbContext(options), IMediaUnitOfWork
{
    public const string SchemaName = "media";

    public override string Schema => SchemaName;

    public DbSet<MediaAsset> Assets => Set<MediaAsset>();

    protected override void ConfigureModule(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new MediaAssetConfiguration());
}

internal sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ToTable("media_assets");
        builder.HasKey(asset => asset.Id);
        builder.Property(asset => asset.Id).ValueGeneratedNever();
        builder.Property(asset => asset.Purpose).HasConversion<string>().HasMaxLength(32);
        builder.Property(asset => asset.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(asset => asset.ContentType).HasMaxLength(64).IsRequired();
        builder.Property(asset => asset.RejectionCode).HasMaxLength(64);
        builder.Ignore(asset => asset.OriginalKey);
        builder.Ignore(asset => asset.ThumbKey);
        builder.Ignore(asset => asset.MediumKey);
        builder.Ignore(asset => asset.AllKeys);

        builder.HasIndex(asset => new { asset.OwnerId, asset.CreatedAt });
        builder.HasIndex(asset => new { asset.Status, asset.CreatedAt });

        // Optimistic concurrency on PostgreSQL's xmin.
        builder.Property<uint>("Version").IsRowVersion().HasColumnName("xmin");
    }
}

/// <summary>
/// Used by <c>dotnet ef migrations</c> only (no database connection is opened):
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Modules/Media/GeoWoof.Modules.Media.Infrastructure --startup-project src/Modules/Media/GeoWoof.Modules.Media.Infrastructure --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class MediaDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MediaDbContext>
{
    public MediaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geowoof_design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", MediaDbContext.SchemaName).UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new MediaDbContext(options);
    }
}

internal sealed class MediaAssetRepository(MediaDbContext dbContext) : IMediaAssetRepository
{
    public Task<MediaAsset?> GetByIdAsync(Guid assetId, CancellationToken cancellationToken) =>
        dbContext.Assets.FirstOrDefaultAsync(asset => asset.Id == assetId, cancellationToken);

    public async Task<IReadOnlyList<MediaAsset>> GetByIdsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
        await dbContext.Assets.Where(asset => assetIds.Contains(asset.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaAsset>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        await dbContext.Assets.Where(asset => asset.OwnerId == ownerId).ToListAsync(cancellationToken);

    public Task<int> CountRequestedSinceAsync(Guid ownerId, DateTimeOffset since, CancellationToken cancellationToken) =>
        dbContext.Assets.CountAsync(asset => asset.OwnerId == ownerId && asset.CreatedAt >= since, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetProcessableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await dbContext.Assets
            .Where(asset => asset.Status == MediaAssetStatus.Processing && (asset.LeaseExpiresAt == null || asset.LeaseExpiresAt < now))
            .OrderBy(asset => asset.CreatedAt)
            .Select(asset => asset.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryClaimAsync(Guid assetId, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        var claimed = await dbContext.Assets
            .Where(asset => asset.Id == assetId
                && asset.Status == MediaAssetStatus.Processing
                && (asset.LeaseExpiresAt == null || asset.LeaseExpiresAt < now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(asset => asset.LeaseExpiresAt, leaseUntil)
                    .SetProperty(asset => asset.ProcessingAttempts, asset => asset.ProcessingAttempts + 1),
                cancellationToken);
        return claimed == 1;
    }

    public async Task<IReadOnlyList<MediaAsset>> GetAbandonedAsync(DateTimeOffset expiredBefore, DateTimeOffset releasedBefore, int limit, CancellationToken cancellationToken) =>
        await dbContext.Assets
            .Where(asset =>
                (asset.Status == MediaAssetStatus.Pending && asset.UploadExpiresAt < expiredBefore)
                || (asset.Status != MediaAssetStatus.Pending && !asset.IsAttached && (asset.ReleasedAt ?? asset.CreatedAt) < releasedBefore))
            .OrderBy(asset => asset.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MediaAsset>> GetWithLeftoverOriginalAsync(int limit, CancellationToken cancellationToken) =>
        await dbContext.Assets
            .Where(asset => !asset.OriginalRemoved && (asset.Status == MediaAssetStatus.Ready || asset.Status == MediaAssetStatus.Rejected))
            .OrderBy(asset => asset.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(MediaAsset asset) => dbContext.Assets.Add(asset);

    public void Remove(MediaAsset asset) => dbContext.Assets.Remove(asset);
}
