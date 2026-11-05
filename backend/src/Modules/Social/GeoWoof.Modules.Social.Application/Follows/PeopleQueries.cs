using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Follows;

/// <summary>
/// The friends, followers or followed of a person. Only that person and their friends can see them:
/// the three lists have the same rule because friends can be worked out from the other two.
/// </summary>
public sealed record GetPeopleQuery(Guid ViewerId, Guid UserId, PeopleList List, int? Limit, string? Cursor) : IQuery<PagedResponse<PersonDto>>;

internal sealed class GetPeopleQueryHandler(IFollowRepository follows, IIdentityApi identity, ISocialGraphCache graphs, UserSummaries summaries)
    : IQueryHandler<GetPeopleQuery, PagedResponse<PersonDto>>
{
    public async Task<Result<PagedResponse<PersonDto>>> Handle(GetPeopleQuery query, CancellationToken cancellationToken)
    {
        var access = await CheckAccessAsync(query, cancellationToken);
        if (access.IsFailure)
        {
            return access.Error;
        }

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
        var rows = await follows.GetPageAsync(query.UserId, query.List, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].Since, page[^1].Id) : null;

        // Those the viewer cannot see, because of a block of their own, are not on the list.
        var graph = await graphs.GetAsync(query.ViewerId, cancellationToken);
        var people = await summaries.LoadAsync(query.ViewerId, page.Select(row => row.UserId).ToList(), cancellationToken);
        var items = page
            .Where(row => people.ContainsKey(row.UserId) && !graph.IsBlockedEitherWay(row.UserId))
            .Select(row => new PersonDto(people[row.UserId], row.Since))
            .ToList();
        return new PagedResponse<PersonDto>(items, next);
    }

    private async Task<Result> CheckAccessAsync(GetPeopleQuery query, CancellationToken cancellationToken)
    {
        if (query.ViewerId == query.UserId)
        {
            return Result.Success();
        }

        var users = await identity.GetPublicUsersAsync([query.UserId], cancellationToken);
        var mine = await graphs.GetAsync(query.ViewerId, cancellationToken);
        if (!users.ContainsKey(query.UserId) || mine.IsBlockedEitherWay(query.UserId))
        {
            return SocialErrors.UserNotFound;
        }

        var theirs = await graphs.GetAsync(query.UserId, cancellationToken);
        var areFriends = mine.Following.Contains(query.UserId) && theirs.Following.Contains(query.ViewerId);
        return areFriends ? Result.Success() : SocialErrors.ListsPrivate;
    }
}

/// <summary>The numbers can be seen by anyone who can see the person at all.</summary>
public sealed record GetCountsQuery(Guid ViewerId, Guid UserId) : IQuery<CountsDto>;

internal sealed class GetCountsQueryHandler(IFollowRepository follows, IIdentityApi identity, ISocialGraphCache graphs)
    : IQueryHandler<GetCountsQuery, CountsDto>
{
    public async Task<Result<CountsDto>> Handle(GetCountsQuery query, CancellationToken cancellationToken)
    {
        if (query.ViewerId != query.UserId)
        {
            var users = await identity.GetPublicUsersAsync([query.UserId], cancellationToken);
            var graph = await graphs.GetAsync(query.ViewerId, cancellationToken);
            if (!users.ContainsKey(query.UserId) || graph.IsBlockedEitherWay(query.UserId))
            {
                return SocialErrors.UserNotFound;
            }
        }

        var counts = await follows.GetCountsAsync(query.UserId, cancellationToken);
        return new CountsDto(counts.Followers, counts.Following, counts.Friends);
    }
}
