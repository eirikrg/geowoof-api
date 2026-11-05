using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Contracts;

namespace GeoWoof.Modules.Identity.Application.Profiles;

public sealed record UserSummaryDto(Guid Id, string Username, string DisplayName, string? City, MediaImage? Avatar);

/// <summary>Finds people by name or username, without the viewer and without anyone blocked in either direction.</summary>
public sealed record SearchUsersQuery(Guid ViewerId, string Q, int? Limit, string? Cursor) : IQuery<PagedResponse<UserSummaryDto>>;

internal sealed class SearchUsersQueryValidator : AbstractValidator<SearchUsersQuery>
{
    public const int MinLength = 2;
    public const int MaxLength = 50;

    public SearchUsersQueryValidator() =>
        RuleFor(query => query.Q)
            .Must(text => text.Trim().Length is >= MinLength and <= MaxLength)
            .WithMessage($"Write between {MinLength} and {MaxLength} characters.");
}

internal sealed class SearchUsersQueryHandler(IUserRepository users, IRelationshipsApi relationships, IMediaApi media)
    : IQueryHandler<SearchUsersQuery, PagedResponse<UserSummaryDto>>
{
    public async Task<Result<PagedResponse<UserSummaryDto>>> Handle(SearchUsersQuery query, CancellationToken cancellationToken)
    {
        var after = RelevancePosition.Start;
        if (query.Cursor is { } cursor)
        {
            if (RelevanceCursor.Decode(cursor) is not { } position)
            {
                return IdentityErrors.InvalidCursor;
            }

            after = position;
        }

        var hidden = await relationships.GetHiddenUserIdsAsync(query.ViewerId, cancellationToken);
        var following = await relationships.GetFollowingIdsAsync(query.ViewerId, cancellationToken);
        var limit = PageLimit.Normalize(query.Limit);
        var hits = await users.SearchAsync(query.Q.Trim(), [.. hidden, query.ViewerId], following, after, limit + 1, cancellationToken);
        var page = hits.Take(limit).ToList();
        var next = hits.Count > limit ? RelevanceCursor.Encode(page[^1]) : null;

        var found = (await users.GetPublicByIdsAsync(page.Select(hit => hit.Id).ToList(), cancellationToken)).ToDictionary(user => user.Id);
        var images = await media.GetImagesAsync(found.Values.Where(user => user.AvatarAssetId is not null).Select(user => user.AvatarAssetId!.Value).ToList(), cancellationToken);
        var items = page
            .Where(hit => found.ContainsKey(hit.Id))
            .Select(hit => found[hit.Id])
            .Select(user =>
            {
                var isRestricted = user.IsPrivate && !following.Contains(user.Id);
                return new UserSummaryDto(
                    user.Id,
                    user.Username!.Value,
                    isRestricted ? user.Username!.Value : user.DisplayName,
                    isRestricted ? null : user.City,
                    user.AvatarAssetId is { } assetId ? images.GetValueOrDefault(assetId) : null);
            })
            .ToList();
        return new PagedResponse<UserSummaryDto>(items, next);
    }
}
