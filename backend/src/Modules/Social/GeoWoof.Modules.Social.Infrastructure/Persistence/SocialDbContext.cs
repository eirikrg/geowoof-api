using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.Modules.Social.Infrastructure.Persistence;

/// <summary>Schema <c>social</c>: friend requests, friendships, blocks and reports.</summary>
public sealed class SocialDbContext(DbContextOptions<SocialDbContext> options) : ModuleDbContext(options), ISocialUnitOfWork
{
    public const string SchemaName = "social";

    public override string Schema => SchemaName;

    public DbSet<FriendRequest> FriendRequests => Set<FriendRequest>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();

    public DbSet<Report> Reports => Set<Report>();

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new FriendRequestConfiguration());
        modelBuilder.ApplyConfiguration(new FriendshipConfiguration());
        modelBuilder.ApplyConfiguration(new UserBlockConfiguration());
        modelBuilder.ApplyConfiguration(new ReportConfiguration());
    }
}

internal sealed class FriendRequestConfiguration : IEntityTypeConfiguration<FriendRequest>
{
    public void Configure(EntityTypeBuilder<FriendRequest> builder)
    {
        builder.ToTable("friend_requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).ValueGeneratedNever();
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(16);

        // The only pending request between two people, whoever sent it.
        builder.HasIndex(request => new { request.UserLowId, request.UserHighId })
            .IsUnique()
            .HasFilter("status = 'Pending'");
        builder.HasIndex(request => new { request.AddresseeId, request.Status, request.CreatedAt, request.Id });
        builder.HasIndex(request => new { request.RequesterId, request.Status, request.CreatedAt, request.Id });
    }
}

internal sealed class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
    public void Configure(EntityTypeBuilder<Friendship> builder)
    {
        builder.ToTable("friendships");
        builder.HasKey(friendship => friendship.Id);
        builder.Property(friendship => friendship.Id).ValueGeneratedNever();
        builder.HasIndex(friendship => new { friendship.UserAId, friendship.UserBId }).IsUnique();
        builder.HasIndex(friendship => friendship.UserBId);
    }
}

internal sealed class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> builder)
    {
        builder.ToTable("user_blocks");
        builder.HasKey(block => block.Id);
        builder.Property(block => block.Id).ValueGeneratedNever();
        builder.HasIndex(block => new { block.BlockerId, block.BlockedId }).IsUnique();
        builder.HasIndex(block => block.BlockedId);
    }
}

internal sealed class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("reports");
        builder.HasKey(report => report.Id);
        builder.Property(report => report.Id).ValueGeneratedNever();
        builder.Property(report => report.TargetType).HasConversion<string>().HasMaxLength(16);
        builder.Property(report => report.Reason).HasConversion<string>().HasMaxLength(32);
        builder.Property(report => report.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(report => report.Details).HasMaxLength(Report.DetailsMaxLength);

        builder.HasIndex(report => new { report.ReporterId, report.TargetType, report.TargetId })
            .IsUnique()
            .HasFilter("status = 'Open'");
        builder.HasIndex(report => report.TargetId);
    }
}

/// <summary>
/// Used by <c>dotnet ef migrations</c> only (no database connection is opened):
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Modules/Social/GeoWoof.Modules.Social.Infrastructure --startup-project src/Modules/Social/GeoWoof.Modules.Social.Infrastructure --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class SocialDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SocialDbContext>
{
    public SocialDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geowoof_design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SocialDbContext.SchemaName).UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new SocialDbContext(options);
    }
}

internal sealed class FriendRequestRepository(SocialDbContext dbContext) : IFriendRequestRepository
{
    public Task<FriendRequest?> GetByIdAsync(Guid requestId, CancellationToken cancellationToken) =>
        dbContext.FriendRequests.FirstOrDefaultAsync(request => request.Id == requestId, cancellationToken);

    public Task<FriendRequest?> GetPendingBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        var pair = new UserPair(first, second);
        return dbContext.FriendRequests.FirstOrDefaultAsync(
            request => request.UserLowId == pair.Low && request.UserHighId == pair.High && request.Status == FriendRequestStatus.Pending,
            cancellationToken);
    }

    public async Task<IReadOnlyList<FriendRequest>> GetPendingPageAsync(Guid userId, RequestDirection direction, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var query = dbContext.FriendRequests.AsNoTracking().Where(request => request.Status == FriendRequestStatus.Pending);
        query = direction == RequestDirection.Received
            ? query.Where(request => request.AddresseeId == userId)
            : query.Where(request => request.RequesterId == userId);
        if (after is not null)
        {
            query = query.Where(request => request.CreatedAt < after.At || (request.CreatedAt == after.At && request.Id.CompareTo(after.Id) < 0));
        }

        return await query.OrderByDescending(request => request.CreatedAt).ThenByDescending(request => request.Id).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FriendRequest>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.FriendRequests.Where(request => request.RequesterId == userId || request.AddresseeId == userId).ToListAsync(cancellationToken);

    public void Add(FriendRequest request) => dbContext.FriendRequests.Add(request);

    public void Remove(FriendRequest request) => dbContext.FriendRequests.Remove(request);
}

internal sealed class FriendshipRepository(SocialDbContext dbContext) : IFriendshipRepository
{
    public Task<Friendship?> GetBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        var pair = new UserPair(first, second);
        return dbContext.Friendships.FirstOrDefaultAsync(friendship => friendship.UserAId == pair.Low && friendship.UserBId == pair.High, cancellationToken);
    }

    public async Task<IReadOnlyList<Friendship>> GetPageAsync(Guid userId, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var query = dbContext.Friendships.AsNoTracking().Where(friendship => friendship.UserAId == userId || friendship.UserBId == userId);
        if (after is not null)
        {
            query = query.Where(friendship => friendship.CreatedAt < after.At || (friendship.CreatedAt == after.At && friendship.Id.CompareTo(after.Id) < 0));
        }

        return await query.OrderByDescending(friendship => friendship.CreatedAt).ThenByDescending(friendship => friendship.Id).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Friendship>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Friendships.Where(friendship => friendship.UserAId == userId || friendship.UserBId == userId).ToListAsync(cancellationToken);

    public void Add(Friendship friendship) => dbContext.Friendships.Add(friendship);

    public void Remove(Friendship friendship) => dbContext.Friendships.Remove(friendship);
}

internal sealed class UserBlockRepository(SocialDbContext dbContext) : IUserBlockRepository
{
    public Task<UserBlock?> GetAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken) =>
        dbContext.UserBlocks.FirstOrDefaultAsync(block => block.BlockerId == blockerId && block.BlockedId == blockedId, cancellationToken);

    public async Task<IReadOnlyList<UserBlock>> GetPageAsync(Guid blockerId, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var query = dbContext.UserBlocks.AsNoTracking().Where(block => block.BlockerId == blockerId);
        if (after is not null)
        {
            query = query.Where(block => block.CreatedAt < after.At || (block.CreatedAt == after.At && block.Id.CompareTo(after.Id) < 0));
        }

        return await query.OrderByDescending(block => block.CreatedAt).ThenByDescending(block => block.Id).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserBlock>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.UserBlocks.Where(block => block.BlockerId == userId || block.BlockedId == userId).ToListAsync(cancellationToken);

    public void Add(UserBlock block) => dbContext.UserBlocks.Add(block);

    public void Remove(UserBlock block) => dbContext.UserBlocks.Remove(block);
}

internal sealed class ReportRepository(SocialDbContext dbContext) : IReportRepository
{
    public Task<Report?> GetOpenAsync(Guid reporterId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken) =>
        dbContext.Reports.FirstOrDefaultAsync(
            report => report.ReporterId == reporterId && report.TargetType == targetType && report.TargetId == targetId && report.Status == ReportStatus.Open,
            cancellationToken);

    public async Task<IReadOnlyList<Report>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Reports.Where(report => report.ReporterId == userId || report.TargetId == userId).ToListAsync(cancellationToken);

    public void Add(Report report) => dbContext.Reports.Add(report);

    public void Remove(Report report) => dbContext.Reports.Remove(report);
}

internal sealed class SocialGraphStore(SocialDbContext dbContext) : ISocialGraphStore
{
    public async Task<SocialGraph> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var friendships = dbContext.Friendships.AsNoTracking();
        var friends = await friendships.Where(friendship => friendship.UserAId == userId).Select(friendship => friendship.UserBId)
            .Union(friendships.Where(friendship => friendship.UserBId == userId).Select(friendship => friendship.UserAId))
            .ToListAsync(cancellationToken);
        var blocks = dbContext.UserBlocks.AsNoTracking();
        var blocked = await blocks.Where(block => block.BlockerId == userId).Select(block => block.BlockedId).ToListAsync(cancellationToken);
        var blockedBy = await blocks.Where(block => block.BlockedId == userId).Select(block => block.BlockerId).ToListAsync(cancellationToken);
        return new SocialGraph(friends, blocked, blockedBy);
    }
}
