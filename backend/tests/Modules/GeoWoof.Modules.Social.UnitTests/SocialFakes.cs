using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.UnitTests;

internal sealed class FakeRequests : IFriendRequestRepository
{
    public List<FriendRequest> Items { get; } = [];

    public Task<FriendRequest?> GetByIdAsync(Guid requestId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(request => request.Id == requestId));

    public Task<FriendRequest?> GetPendingBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        var pair = new UserPair(first, second);
        return Task.FromResult(Items.Find(request => request.UserLowId == pair.Low && request.UserHighId == pair.High && request.Status == FriendRequestStatus.Pending));
    }

    public Task<IReadOnlyList<FriendRequest>> GetPendingPageAsync(Guid userId, RequestDirection direction, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        IReadOnlyList<FriendRequest> found = Items
            .Where(request => request.Status == FriendRequestStatus.Pending)
            .Where(request => direction == RequestDirection.Received ? request.AddresseeId == userId : request.RequesterId == userId)
            .OrderByDescending(request => request.CreatedAt)
            .ThenByDescending(request => request.Id)
            .Where(request => after is null || request.CreatedAt < after.At || (request.CreatedAt == after.At && request.Id.CompareTo(after.Id) < 0))
            .Take(take)
            .ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<FriendRequest>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<FriendRequest> found = Items.Where(request => request.RequesterId == userId || request.AddresseeId == userId).ToList();
        return Task.FromResult(found);
    }

    public void Add(FriendRequest request) => Items.Add(request);

    public void Remove(FriendRequest request) => Items.Remove(request);
}

internal sealed class FakeFriendships : IFriendshipRepository
{
    public List<Friendship> Items { get; } = [];

    public Task<Friendship?> GetBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        var pair = new UserPair(first, second);
        return Task.FromResult(Items.Find(friendship => friendship.UserAId == pair.Low && friendship.UserBId == pair.High));
    }

    public Task<IReadOnlyList<Friendship>> GetPageAsync(Guid userId, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        IReadOnlyList<Friendship> found = Items
            .Where(friendship => friendship.UserAId == userId || friendship.UserBId == userId)
            .OrderByDescending(friendship => friendship.CreatedAt)
            .ThenByDescending(friendship => friendship.Id)
            .Where(friendship => after is null || friendship.CreatedAt < after.At || (friendship.CreatedAt == after.At && friendship.Id.CompareTo(after.Id) < 0))
            .Take(take)
            .ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Friendship>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Friendship> found = Items.Where(friendship => friendship.UserAId == userId || friendship.UserBId == userId).ToList();
        return Task.FromResult(found);
    }

    public void Add(Friendship friendship) => Items.Add(friendship);

    public void Remove(Friendship friendship) => Items.Remove(friendship);
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
internal sealed class FakeGraphs(FakeFriendships friendships, FakeBlocks blocks) : ISocialGraphCache
{
    public List<Guid> Invalidated { get; } = [];

    public ValueTask<SocialGraph> GetAsync(Guid userId, CancellationToken cancellationToken) => ValueTask.FromResult(new SocialGraph(
        friendships.Items.Where(friendship => friendship.UserAId == userId || friendship.UserBId == userId).Select(friendship => friendship.OtherThan(userId)).ToList(),
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

    public void Add(Guid id, string username) => Users[id] = new UserSummary(id, username, username, null, null);

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
