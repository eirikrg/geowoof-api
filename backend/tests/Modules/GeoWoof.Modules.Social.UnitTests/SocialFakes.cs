using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.UnitTests;

internal sealed class FakeFollows : IFollowRepository
{
    public List<Follow> Items { get; } = [];

    public Task<Follow?> GetAsync(Guid followerId, Guid followeeId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(follow => follow.FollowerId == followerId && follow.FolloweeId == followeeId));

    public Task<IReadOnlyList<PersonRow>> GetPageAsync(Guid userId, PeopleList list, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var rows = list switch
        {
            PeopleList.Followers => Items.Where(follow => follow.FolloweeId == userId).Select(follow => new PersonRow(follow.FollowerId, follow.CreatedAt, follow.Id)),
            PeopleList.Following => Items.Where(follow => follow.FollowerId == userId).Select(follow => new PersonRow(follow.FolloweeId, follow.CreatedAt, follow.Id)),
            _ => Items
                .Where(follow => follow.FollowerId == userId && Items.Exists(other => other.FollowerId == follow.FolloweeId && other.FolloweeId == userId))
                .Select(follow => new PersonRow(follow.FolloweeId, follow.CreatedAt, follow.Id)),
        };
        IReadOnlyList<PersonRow> found = rows
            .OrderByDescending(row => row.Since)
            .ThenByDescending(row => row.Id)
            .Where(row => after is null || row.Since < after.At || (row.Since == after.At && row.Id.CompareTo(after.Id) < 0))
            .Take(take)
            .ToList();
        return Task.FromResult(found);
    }

    public Task<SocialCounts> GetCountsAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(new SocialCounts(
        Items.Count(follow => follow.FolloweeId == userId),
        Items.Count(follow => follow.FollowerId == userId),
        Items.Count(follow => follow.FollowerId == userId && Items.Exists(other => other.FollowerId == follow.FolloweeId && other.FolloweeId == userId))));

    public Task<IReadOnlyList<Follow>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Follow> found = Items.Where(follow => follow.FollowerId == userId || follow.FolloweeId == userId).ToList();
        return Task.FromResult(found);
    }

    public void Add(Follow follow) => Items.Add(follow);

    public void Remove(Follow follow) => Items.Remove(follow);
}

internal sealed class FakeRequests : IFollowRequestRepository
{
    public List<FollowRequest> Items { get; } = [];

    public Task<FollowRequest?> GetByIdAsync(Guid requestId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(request => request.Id == requestId));

    public Task<FollowRequest?> GetPendingAsync(Guid requesterId, Guid targetId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(request => request.RequesterId == requesterId && request.TargetId == targetId && request.Status == FollowRequestStatus.Pending));

    public Task<IReadOnlyList<FollowRequest>> GetPendingPageAsync(Guid userId, RequestDirection direction, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        IReadOnlyList<FollowRequest> found = Items
            .Where(request => request.Status == FollowRequestStatus.Pending)
            .Where(request => direction == RequestDirection.Received ? request.TargetId == userId : request.RequesterId == userId)
            .OrderByDescending(request => request.CreatedAt)
            .ThenByDescending(request => request.Id)
            .Where(request => after is null || request.CreatedAt < after.At || (request.CreatedAt == after.At && request.Id.CompareTo(after.Id) < 0))
            .Take(take)
            .ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<FollowRequest>> GetAllPendingForAsync(Guid targetId, CancellationToken cancellationToken)
    {
        IReadOnlyList<FollowRequest> found = Items.Where(request => request.TargetId == targetId && request.Status == FollowRequestStatus.Pending).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<FollowRequest>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<FollowRequest> found = Items.Where(request => request.RequesterId == userId || request.TargetId == userId).ToList();
        return Task.FromResult(found);
    }

    public void Add(FollowRequest request) => Items.Add(request);

    public void Remove(FollowRequest request) => Items.Remove(request);
}

internal sealed class FakeBlocks : IUserBlockRepository
{
    public List<UserBlock> Items { get; } = [];

    public Task<UserBlock?> GetAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(block => block.BlockerId == blockerId && block.BlockedId == blockedId));

    public Task<IReadOnlyList<UserBlock>> GetPageAsync(Guid blockerId, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        IReadOnlyList<UserBlock> found = Items
            .Where(block => block.BlockerId == blockerId)
            .OrderByDescending(block => block.CreatedAt)
            .ThenByDescending(block => block.Id)
            .Where(block => after is null || block.CreatedAt < after.At || (block.CreatedAt == after.At && block.Id.CompareTo(after.Id) < 0))
            .Take(take)
            .ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<UserBlock>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<UserBlock> found = Items.Where(block => block.BlockerId == userId || block.BlockedId == userId).ToList();
        return Task.FromResult(found);
    }

    public void Add(UserBlock block) => Items.Add(block);

    public void Remove(UserBlock block) => Items.Remove(block);
}

internal sealed class FakeReports : IReportRepository
{
    public List<Report> Items { get; } = [];

    public Task<Report?> GetOpenAsync(Guid reporterId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(report => report.ReporterId == reporterId && report.TargetType == targetType && report.TargetId == targetId));

    public Task<IReadOnlyList<Report>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Report> found = Items.Where(report => report.ReporterId == userId || report.TargetId == userId).ToList();
        return Task.FromResult(found);
    }

    public void Add(Report report) => Items.Add(report);

    public void Remove(Report report) => Items.Remove(report);
}

/// <summary>Reads the relationships as they are, and remembers who was invalidated.</summary>
internal sealed class FakeGraphs(FakeFollows follows, FakeBlocks blocks) : ISocialGraphCache
{
    public List<Guid> Invalidated { get; } = [];

    public ValueTask<SocialGraph> GetAsync(Guid userId, CancellationToken cancellationToken) => ValueTask.FromResult(new SocialGraph(
        follows.Items.Where(follow => follow.FollowerId == userId).Select(follow => follow.FolloweeId).ToList(),
        blocks.Items.Where(block => block.BlockerId == userId).Select(block => block.BlockedId).ToList(),
        blocks.Items.Where(block => block.BlockedId == userId).Select(block => block.BlockerId).ToList()));

    public ValueTask InvalidateAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        Invalidated.Add(first);
        Invalidated.Add(second);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork : ISocialUnitOfWork
{
    public int Saves { get; private set; }

    public bool FailNextSave { get; set; }

    /// <summary>What a concurrent request did while this one was running.</summary>
    public Action? OnDiscard { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new UniqueConstraintViolationException("uq", new InvalidOperationException());
        }

        Saves++;
        return Task.FromResult(1);
    }

    public void DiscardChanges() => OnDiscard?.Invoke();
}

internal sealed class FakeIdentity : IIdentityApi
{
    public Dictionary<Guid, UserSummary> Users { get; } = [];

    public void Add(Guid id, string username, bool isPrivate = false) =>
        Users[id] = new UserSummary(id, username, $"Name of {username}", "Madrid", null, isPrivate);

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<IReadOnlyDictionary<Guid, UserSummary>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, UserSummary> found = userIds.Distinct().Where(Users.ContainsKey).ToDictionary(id => id, id => Users[id]);
        return Task.FromResult(found);
    }
}

internal sealed class FakeMedia : IMediaApi
{
    public Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken) =>
        Task.FromResult<MediaAttachFailure?>(null);

    public Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.CreateVersion7());

    public Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, MediaImage> empty = new Dictionary<Guid, MediaImage>();
        return Task.FromResult(empty);
    }
}
