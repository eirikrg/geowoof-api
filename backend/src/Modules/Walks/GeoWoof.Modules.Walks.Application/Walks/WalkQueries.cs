using System.Globalization;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Social.Contracts;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Domain;

namespace GeoWoof.Modules.Walks.Application.Walks;

internal static class ViewerRelations
{
    public static ViewerRelation ToViewerRelation(this Relationship relationship) => relationship switch
    {
        Relationship.Self => ViewerRelation.Owner,
        Relationship.Friend => ViewerRelation.Friend,
        Relationship.None => ViewerRelation.Stranger,
        _ => ViewerRelation.Blocked,
    };
}

/// <summary>For whoever cannot see it, the walk does not exist (docs/api.md).</summary>
public sealed record GetWalkQuery(Guid ViewerId, Guid WalkId) : IQuery<WalkDto>;

internal sealed class GetWalkQueryHandler(IWalkRepository walks, IRelationshipsApi relationships, IMediaApi media) : IQueryHandler<GetWalkQuery, WalkDto>
{
    public async Task<Result<WalkDto>> Handle(GetWalkQuery query, CancellationToken cancellationToken)
    {
        var walk = await walks.GetByIdAsync(query.WalkId, cancellationToken);
        if (walk is null)
        {
            return WalksErrors.NotFound;
        }

        var relationship = await relationships.GetRelationshipAsync(query.ViewerId, walk.OwnerId, cancellationToken);
        return walk.IsVisibleTo(relationship.ToViewerRelation())
            ? await WalkMapper.ToDtoAsync(walk, query.ViewerId, media, cancellationToken)
            : WalksErrors.NotFound;
    }
}

/// <summary>The walks of a person that the viewer can see, without the zones that reveal where they live.</summary>
public sealed record GetUserWalksQuery(Guid ViewerId, Guid UserId, int? Limit, string? Cursor) : IQuery<PagedResponse<WalkSummaryDto>>;

internal sealed class GetUserWalksQueryHandler(IWalkRepository walks, IIdentityApi identity, IRelationshipsApi relationships, IMediaApi media)
    : IQueryHandler<GetUserWalksQuery, PagedResponse<WalkSummaryDto>>
{
    public async Task<Result<PagedResponse<WalkSummaryDto>>> Handle(GetUserWalksQuery query, CancellationToken cancellationToken)
    {
        var viewer = (await relationships.GetRelationshipAsync(query.ViewerId, query.UserId, cancellationToken)).ToViewerRelation();
        var users = await identity.GetPublicUsersAsync([query.UserId], cancellationToken);
        if (viewer == ViewerRelation.Blocked || !users.ContainsKey(query.UserId))
        {
            return WalksErrors.UserNotFound;
        }

        WalkPageCursor? after = null;
        if (query.Cursor is { } cursor)
        {
            after = WalkCursor.Decode(cursor);
            if (after is null)
            {
                return WalksErrors.InvalidCursor;
            }
        }

        var limit = PageLimit.Normalize(query.Limit);
        var rows = await walks.GetPageAsync(query.UserId, null, VisibilityRules.VisibleTo(viewer), after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? WalkCursor.Encode(page[^1]) : null;

        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? previews = null;
        if (viewer != ViewerRelation.Owner)
        {
            var tracks = await walks.GetTracksAsync(page.Select(row => row.Id).ToList(), cancellationToken);
            previews = tracks.ToDictionary(track => track.Key, track => RoutePrivacy.Hide(track.Value).Previews);
        }

        return new PagedResponse<WalkSummaryDto>(await WalkMapper.ToSummariesAsync(page, previews, media, cancellationToken), next);
    }
}

public sealed record DeleteWalkCommand(Guid UserId, Guid WalkId) : ICommand;

internal sealed class DeleteWalkCommandHandler(IWalkRepository walks, IMediaApi media, IWalksUnitOfWork unitOfWork) : ICommandHandler<DeleteWalkCommand>
{
    public async Task<Result> Handle(DeleteWalkCommand command, CancellationToken cancellationToken)
    {
        var walk = await walks.GetByIdAsync(command.WalkId, cancellationToken);
        if (walk is null || walk.OwnerId != command.UserId)
        {
            return WalksErrors.NotFound;
        }

        var photos = walk.PhotoAssetIds;
        walk.Delete();
        walks.Remove(walk);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var photo in photos)
        {
            await media.ReleaseAsync(photo, walk.OwnerId, cancellationToken);
        }

        return Result.Success();
    }
}

/// <summary>The walks of the viewer, optionally only those of one of their pets.</summary>
public sealed record GetWalksQuery(Guid ViewerId, Guid? PetId, int? Limit, string? Cursor) : IQuery<PagedResponse<WalkSummaryDto>>;

internal sealed class GetWalksQueryHandler(IWalkRepository walks, IPetsApi pets, IMediaApi media)
    : IQueryHandler<GetWalksQuery, PagedResponse<WalkSummaryDto>>
{
    public async Task<Result<PagedResponse<WalkSummaryDto>>> Handle(GetWalksQuery query, CancellationToken cancellationToken)
    {
        var access = await PetAccess.CheckAsync(pets, query.PetId, query.ViewerId, cancellationToken);
        if (access.IsFailure)
        {
            return access.Error;
        }

        WalkPageCursor? after = null;
        if (query.Cursor is { } cursor)
        {
            after = WalkCursor.Decode(cursor);
            if (after is null)
            {
                return WalksErrors.InvalidCursor;
            }
        }

        var limit = PageLimit.Normalize(query.Limit);
        var rows = await walks.GetPageAsync(query.ViewerId, query.PetId, null, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? WalkCursor.Encode(page[^1]) : null;
        return new PagedResponse<WalkSummaryDto>(await WalkMapper.ToSummariesAsync(page, null, media, cancellationToken), next);
    }
}

/// <summary>The totals of the viewer, optionally only those of one of their pets.</summary>
public sealed record GetWalkStatsQuery(Guid ViewerId, Guid? PetId) : IQuery<WalkStatsDto>;

internal sealed class GetWalkStatsQueryHandler(IWalkRepository walks, IPetsApi pets, TimeProvider timeProvider) : IQueryHandler<GetWalkStatsQuery, WalkStatsDto>
{
    public async Task<Result<WalkStatsDto>> Handle(GetWalkStatsQuery query, CancellationToken cancellationToken)
    {
        var access = await PetAccess.CheckAsync(pets, query.PetId, query.ViewerId, cancellationToken);
        if (access.IsFailure)
        {
            return access.Error;
        }

        var rows = await walks.GetStatsRowsAsync(query.ViewerId, query.PetId, cancellationToken);
        var moments = rows.Select(row => new WalkMoment(row.StartedAt, row.TimeZone)).ToList();
        return new WalkStatsDto(
            rows.Count,
            rows.Sum(row => row.DistanceM),
            rows.Sum(row => row.DurationS),
            rows.Sum(row => row.MovingTimeS),
            WeeklyStreak.Calculate(moments, timeProvider.GetUtcNow()));
    }
}

internal static class PetAccess
{
    public static async Task<Result> CheckAsync(IPetsApi pets, Guid? petId, Guid viewerId, CancellationToken cancellationToken)
    {
        if (petId is not { } id)
        {
            return Result.Success();
        }

        var owner = await pets.FindOwnerAsync(id, cancellationToken);
        if (owner is null)
        {
            return WalksErrors.PetNotFound;
        }

        return owner == viewerId ? Result.Success() : WalksErrors.NotPetOwner;
    }
}

internal static class WalkCursor
{
    private const char Separator = '|';

    public static string Encode(WalkSummaryRow row) =>
        PageCursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{row.StartedAt.UtcTicks}{Separator}{row.Id}"));

    public static WalkPageCursor? Decode(string cursor)
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
                ? new WalkPageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
    }
}
