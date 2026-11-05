using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.Modules.Social.Infrastructure.Persistence;

/// <summary>Schema <c>social</c>: follows, follow requests, blocks, reports, posts and what people do with them.</summary>
public sealed class SocialDbContext(DbContextOptions<SocialDbContext> options) : ModuleDbContext(options), ISocialUnitOfWork
{
    public const string SchemaName = "social";

    public override string Schema => SchemaName;

    public DbSet<Follow> Follows => Set<Follow>();

    public DbSet<FollowRequest> FollowRequests => Set<FollowRequest>();

    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();

    public DbSet<Report> Reports => Set<Report>();

    public DbSet<Post> Posts => Set<Post>();

    public DbSet<PostPet> PostPets => Set<PostPet>();

    public DbSet<PostMention> PostMentions => Set<PostMention>();

    public DbSet<PostLike> PostLikes => Set<PostLike>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<CommentMention> CommentMentions => Set<CommentMention>();

    public DbSet<Repost> Reposts => Set<Repost>();

    public DbSet<SavedPost> SavedPosts => Set<SavedPost>();

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new FollowConfiguration());
        modelBuilder.ApplyConfiguration(new FollowRequestConfiguration());
        modelBuilder.ApplyConfiguration(new UserBlockConfiguration());
        modelBuilder.ApplyConfiguration(new ReportConfiguration());
        modelBuilder.ApplyConfiguration(new PostConfiguration());
        modelBuilder.ApplyConfiguration(new PostMediaConfiguration());
        modelBuilder.ApplyConfiguration(new PostPetConfiguration());
        modelBuilder.ApplyConfiguration(new PostMentionConfiguration());
        modelBuilder.ApplyConfiguration(new PostLikeConfiguration());
        modelBuilder.ApplyConfiguration(new CommentConfiguration());
        modelBuilder.ApplyConfiguration(new CommentMentionConfiguration());
        modelBuilder.ApplyConfiguration(new RepostConfiguration());
        modelBuilder.ApplyConfiguration(new SavedPostConfiguration());
    }
}

internal sealed class FollowConfiguration : IEntityTypeConfiguration<Follow>
{
    public void Configure(EntityTypeBuilder<Follow> builder)
    {
        builder.ToTable("follows");
        builder.HasKey(follow => follow.Id);
        builder.Property(follow => follow.Id).ValueGeneratedNever();
        builder.HasIndex(follow => new { follow.FollowerId, follow.FolloweeId }).IsUnique();
        builder.HasIndex(follow => new { follow.FollowerId, follow.CreatedAt, follow.Id });
        builder.HasIndex(follow => new { follow.FolloweeId, follow.CreatedAt, follow.Id });
    }
}

internal sealed class FollowRequestConfiguration : IEntityTypeConfiguration<FollowRequest>
{
    public void Configure(EntityTypeBuilder<FollowRequest> builder)
    {
        builder.ToTable("follow_requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).ValueGeneratedNever();
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(16);

        // Each person can have one pending request to another.
        builder.HasIndex(request => new { request.RequesterId, request.TargetId })
            .IsUnique()
            .HasFilter("status = 'Pending'");
        builder.HasIndex(request => new { request.TargetId, request.Status, request.CreatedAt, request.Id });
        builder.HasIndex(request => new { request.RequesterId, request.Status, request.CreatedAt, request.Id });
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

internal sealed class FollowRepository(SocialDbContext dbContext) : IFollowRepository
{
    public Task<Follow?> GetAsync(Guid followerId, Guid followeeId, CancellationToken cancellationToken) =>
        dbContext.Follows.FirstOrDefaultAsync(follow => follow.FollowerId == followerId && follow.FolloweeId == followeeId, cancellationToken);

    public async Task<IReadOnlyList<PersonRow>> GetPageAsync(Guid userId, PeopleList list, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        // An anonymous type, because EF cannot filter or sort by a member of a record built through its constructor.
        var rows = list switch
        {
            PeopleList.Followers => dbContext.Follows.AsNoTracking()
                .Where(follow => follow.FolloweeId == userId)
                .Select(follow => new { UserId = follow.FollowerId, Since = follow.CreatedAt, follow.Id }),
            PeopleList.Following => dbContext.Follows.AsNoTracking()
                .Where(follow => follow.FollowerId == userId)
                .Select(follow => new { UserId = follow.FolloweeId, Since = follow.CreatedAt, follow.Id }),
            _ => from mine in dbContext.Follows.AsNoTracking()
                 join theirs in dbContext.Follows.AsNoTracking()
                     on new { Other = mine.FolloweeId, Me = mine.FollowerId } equals new { Other = theirs.FollowerId, Me = theirs.FolloweeId }
                 where mine.FollowerId == userId
                 select new { UserId = mine.FolloweeId, Since = mine.CreatedAt > theirs.CreatedAt ? mine.CreatedAt : theirs.CreatedAt, mine.Id },
        };
        if (after is not null)
        {
            rows = rows.Where(row => row.Since < after.At || (row.Since == after.At && row.Id.CompareTo(after.Id) < 0));
        }

        return await rows
            .OrderByDescending(row => row.Since).ThenByDescending(row => row.Id)
            .Take(take)
            .Select(row => new PersonRow(row.UserId, row.Since, row.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<Guid>> GetFollowersAmongAsync(Guid userId, IReadOnlyCollection<Guid> candidateIds, CancellationToken cancellationToken) =>
        await dbContext.Follows.AsNoTracking()
            .Where(follow => follow.FolloweeId == userId && candidateIds.Contains(follow.FollowerId))
            .Select(follow => follow.FollowerId)
            .ToListAsync(cancellationToken);

    public async Task<SocialCounts> GetCountsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var follows = dbContext.Follows.AsNoTracking();
        var followers = await follows.CountAsync(follow => follow.FolloweeId == userId, cancellationToken);
        var following = await follows.CountAsync(follow => follow.FollowerId == userId, cancellationToken);
        var friends = await follows
            .Where(mine => mine.FollowerId == userId)
            .CountAsync(mine => follows.Any(theirs => theirs.FollowerId == mine.FolloweeId && theirs.FolloweeId == userId), cancellationToken);
        return new SocialCounts(followers, following, friends);
    }

    public async Task<IReadOnlyList<Follow>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Follows.Where(follow => follow.FollowerId == userId || follow.FolloweeId == userId).ToListAsync(cancellationToken);

    public void Add(Follow follow) => dbContext.Follows.Add(follow);

    public void Remove(Follow follow) => dbContext.Follows.Remove(follow);
}

internal sealed class FollowRequestRepository(SocialDbContext dbContext) : IFollowRequestRepository
{
    public Task<FollowRequest?> GetByIdAsync(Guid requestId, CancellationToken cancellationToken) =>
        dbContext.FollowRequests.FirstOrDefaultAsync(request => request.Id == requestId, cancellationToken);

    public Task<FollowRequest?> GetPendingAsync(Guid requesterId, Guid targetId, CancellationToken cancellationToken) =>
        dbContext.FollowRequests.FirstOrDefaultAsync(
            request => request.RequesterId == requesterId && request.TargetId == targetId && request.Status == FollowRequestStatus.Pending,
            cancellationToken);

    public async Task<IReadOnlyList<FollowRequest>> GetPendingPageAsync(Guid userId, RequestDirection direction, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var query = dbContext.FollowRequests.AsNoTracking().Where(request => request.Status == FollowRequestStatus.Pending);
        query = direction == RequestDirection.Received
            ? query.Where(request => request.TargetId == userId)
            : query.Where(request => request.RequesterId == userId);
        if (after is not null)
        {
            query = query.Where(request => request.CreatedAt < after.At || (request.CreatedAt == after.At && request.Id.CompareTo(after.Id) < 0));
        }

        return await query.OrderByDescending(request => request.CreatedAt).ThenByDescending(request => request.Id).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FollowRequest>> GetAllPendingForAsync(Guid targetId, CancellationToken cancellationToken) =>
        await dbContext.FollowRequests.Where(request => request.TargetId == targetId && request.Status == FollowRequestStatus.Pending).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FollowRequest>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.FollowRequests.Where(request => request.RequesterId == userId || request.TargetId == userId).ToListAsync(cancellationToken);

    public void Add(FollowRequest request) => dbContext.FollowRequests.Add(request);

    public void Remove(FollowRequest request) => dbContext.FollowRequests.Remove(request);
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
        var following = await dbContext.Follows.AsNoTracking().Where(follow => follow.FollowerId == userId).Select(follow => follow.FolloweeId).ToListAsync(cancellationToken);
        var blocks = dbContext.UserBlocks.AsNoTracking();
        var blocked = await blocks.Where(block => block.BlockerId == userId).Select(block => block.BlockedId).ToListAsync(cancellationToken);
        var blockedBy = await blocks.Where(block => block.BlockedId == userId).Select(block => block.BlockerId).ToListAsync(cancellationToken);
        return new SocialGraph(following, blocked, blockedBy);
    }
}
