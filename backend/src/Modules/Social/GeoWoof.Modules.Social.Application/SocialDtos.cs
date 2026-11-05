using System.Globalization;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application;

public sealed record UserSummaryDto(Guid Id, string Username, string DisplayName, string? City, MediaImage? Avatar);

public sealed record FollowRequestDto(Guid Id, UserSummaryDto User, string Direction, DateTimeOffset CreatedAt);

public sealed record PersonDto(UserSummaryDto User, DateTimeOffset Since);

public sealed record BlockDto(UserSummaryDto User, DateTimeOffset Since);

public sealed record CountsDto(int Followers, int Following, int Friends);

public sealed record RelationshipDto(string Status, bool FollowsYou, Guid? RequestId, Guid? IncomingRequestId);

public sealed record ReportDto(Guid Id);

/// <summary>Builds the people of a list for one viewer, with the signed URLs of their avatars.</summary>
internal sealed class UserSummaries(IIdentityApi identity, ISocialGraphCache graphs, IMediaApi media)
{
    /// <summary>An account being deleted, whose event has not arrived yet, is left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, UserSummaryDto>> LoadAsync(Guid viewerId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var users = await identity.GetPublicUsersAsync(userIds, cancellationToken);
        var graph = await graphs.GetAsync(viewerId, cancellationToken);
        var assetIds = users.Values.Where(user => user.AvatarAssetId is not null).Select(user => user.AvatarAssetId!.Value).ToList();
        var images = await media.GetImagesAsync(assetIds, cancellationToken);
        return users.Values.ToDictionary(
            user => user.Id,
            user =>
            {
                var seen = user.IsPrivate && user.Id != viewerId && !graph.Following.Contains(user.Id) ? user.Restricted() : user;
                return new UserSummaryDto(
                    seen.Id,
                    seen.Username,
                    seen.DisplayName,
                    seen.City,
                    seen.AvatarAssetId is { } assetId ? images.GetValueOrDefault(assetId) : null);
            });
    }

    public async Task<UserSummaryDto?> FindAsync(Guid viewerId, Guid userId, CancellationToken cancellationToken) =>
        (await LoadAsync(viewerId, [userId], cancellationToken)).GetValueOrDefault(userId);
}

/// <summary>What one person has to do with another, as the API shows it.</summary>
internal sealed class RelationshipReader(ISocialGraphCache graphs, IFollowRequestRepository requests)
{
    private const string Self = "Self";
    private const string None = "None";
    private const string Requested = "Requested";
    private const string Following = "Following";
    private const string Friends = "Friends";
    private const string Blocked = "Blocked";

    /// <summary>For whoever is blocked, the other person does not exist: they cannot tell the difference.</summary>
    public async Task<BuildingBlocks.Domain.Results.Result<RelationshipDto>> ReadAsync(Guid viewerId, Guid userId, CancellationToken cancellationToken)
    {
        if (viewerId == userId)
        {
            return new RelationshipDto(Self, false, null, null);
        }

        var mine = await graphs.GetAsync(viewerId, cancellationToken);
        if (mine.Blocked.Contains(userId))
        {
            return new RelationshipDto(Blocked, false, null, null);
        }

        if (mine.BlockedBy.Contains(userId))
        {
            return SocialErrors.UserNotFound;
        }

        var theirs = await graphs.GetAsync(userId, cancellationToken);
        var iFollow = mine.Following.Contains(userId);
        var followsYou = theirs.Following.Contains(viewerId);
        var outgoing = iFollow ? null : await requests.GetPendingAsync(viewerId, userId, cancellationToken);
        var incoming = followsYou ? null : await requests.GetPendingAsync(userId, viewerId, cancellationToken);
        var status = (iFollow, followsYou) switch
        {
            (true, true) => Friends,
            (true, false) => Following,
            _ => outgoing is null ? None : Requested,
        };
        return new RelationshipDto(status, followsYou, outgoing?.Id, incoming?.Id);
    }
}

/// <summary>Following and stopping, in one place: this decides when a friendship starts and ends.</summary>
internal sealed class FollowService(IFollowRepository follows, TimeProvider timeProvider)
{
    public async Task StartAsync(Guid followerId, Guid followeeId, CancellationToken cancellationToken)
    {
        if (await follows.GetAsync(followerId, followeeId, cancellationToken) is not null)
        {
            return;
        }

        var theyFollowBack = await follows.GetAsync(followeeId, followerId, cancellationToken) is not null;
        var now = timeProvider.GetUtcNow();
        follows.Add(Follow.Start(Guid.CreateVersion7(now), followerId, followeeId, theyFollowBack, now));
    }

    public async Task EndAsync(Follow follow, CancellationToken cancellationToken)
    {
        var theyFollowBack = await follows.GetAsync(follow.FolloweeId, follow.FollowerId, cancellationToken) is not null;
        follow.End(theyFollowBack);
        follows.Remove(follow);
    }
}

internal static class SocialCursor
{
    private const char Separator = '|';

    public static string Encode(DateTimeOffset at, Guid id) =>
        PageCursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}{Separator}{id}"));

    public static SocialPosition? Decode(string cursor)
    {
        if (!PageCursor.TryDecode(cursor, out var value))
        {
            return null;
        }

        var parts = value.Split(Separator);
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && Guid.TryParse(parts[1], out var id)
                ? new SocialPosition(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
    }
}
