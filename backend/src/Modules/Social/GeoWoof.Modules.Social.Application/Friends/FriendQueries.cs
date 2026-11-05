using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Friends;

public sealed record GetFriendsQuery(Guid UserId, int? Limit, string? Cursor) : IQuery<PagedResponse<FriendDto>>;

internal sealed class GetFriendsQueryHandler(IFriendshipRepository friendships, ISocialGraphCache graphs, UserSummaries summaries)
    : IQueryHandler<GetFriendsQuery, PagedResponse<FriendDto>>
{
    public async Task<Result<PagedResponse<FriendDto>>> Handle(GetFriendsQuery query, CancellationToken cancellationToken)
    {
        SocialPosition? after = null;
        if (query.Cursor is { } cursor)
        {
            after = SocialCursor.Decode(cursor);
            if (after is null)
            {
                return SocialErrors.InvalidCursor;
            }
        }

        var limit = PageLimit.Normalize(query.Limit);
        var rows = await friendships.GetPageAsync(query.UserId, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;

        // A block removes the friendship, but one can be accepted just while blocking.
        var graph = await graphs.GetAsync(query.UserId, cancellationToken);
        var people = await summaries.LoadAsync(page.Select(friendship => friendship.OtherThan(query.UserId)).ToList(), cancellationToken);
        var items = page
            .Where(friendship => people.ContainsKey(friendship.OtherThan(query.UserId)) && !graph.IsBlockedEitherWay(friendship.OtherThan(query.UserId)))
            .Select(friendship => new FriendDto(people[friendship.OtherThan(query.UserId)], friendship.CreatedAt))
            .ToList();
        return new PagedResponse<FriendDto>(items, next);
    }
}

public sealed record RemoveFriendCommand(Guid UserId, Guid FriendId) : ICommand;

internal sealed class RemoveFriendCommandHandler(IFriendshipRepository friendships, ISocialGraphCache graphs, ISocialUnitOfWork unitOfWork)
    : ICommandHandler<RemoveFriendCommand>
{
    public async Task<Result> Handle(RemoveFriendCommand command, CancellationToken cancellationToken)
    {
        var friendship = await friendships.GetBetweenAsync(command.UserId, command.FriendId, cancellationToken);
        if (friendship is null)
        {
            return SocialErrors.NotFriends;
        }

        friendship.End();
        friendships.Remove(friendship);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await graphs.InvalidateAsync(command.UserId, command.FriendId, cancellationToken);
        return Result.Success();
    }
}

public sealed record GetRelationshipQuery(Guid ViewerId, Guid UserId) : IQuery<RelationshipDto>;

internal sealed class GetRelationshipQueryHandler(IFriendRequestRepository requests, ISocialGraphCache graphs, UserSummaries summaries)
    : IQueryHandler<GetRelationshipQuery, RelationshipDto>
{
    private const string Self = "Self";
    private const string None = "None";
    private const string Friends = "Friends";
    private const string RequestSent = "RequestSent";
    private const string RequestReceived = "RequestReceived";
    private const string Blocked = "Blocked";

    public async Task<Result<RelationshipDto>> Handle(GetRelationshipQuery query, CancellationToken cancellationToken)
    {
        if (query.ViewerId == query.UserId)
        {
            return new RelationshipDto(Self, null);
        }

        if (await summaries.FindAsync(query.UserId, cancellationToken) is null)
        {
            return SocialErrors.UserNotFound;
        }

        var graph = await graphs.GetAsync(query.ViewerId, cancellationToken);
        if (graph.Blocked.Contains(query.UserId))
        {
            return new RelationshipDto(Blocked, null);
        }

        // For whoever is blocked, the other person does not exist: they cannot tell the difference.
        if (graph.BlockedBy.Contains(query.UserId))
        {
            return SocialErrors.UserNotFound;
        }

        if (graph.Friends.Contains(query.UserId))
        {
            return new RelationshipDto(Friends, null);
        }

        var pending = await requests.GetPendingBetweenAsync(query.ViewerId, query.UserId, cancellationToken);
        return pending switch
        {
            null => new RelationshipDto(None, null),
            _ when pending.RequesterId == query.ViewerId => new RelationshipDto(RequestSent, pending.Id),
            _ => new RelationshipDto(RequestReceived, pending.Id),
        };
    }
}
