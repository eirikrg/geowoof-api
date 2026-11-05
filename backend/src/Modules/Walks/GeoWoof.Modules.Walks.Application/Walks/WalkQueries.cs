using System.Globalization;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Domain;

namespace GeoWoof.Modules.Walks.Application.Walks;

/// <summary>In this phase only the owner can see a walk: for anyone else, it does not exist (docs/api.md).</summary>
public sealed record GetWalkQuery(Guid ViewerId, Guid WalkId) : IQuery<WalkDto>;

internal sealed class GetWalkQueryHandler(IWalkRepository walks, IMediaApi media) : IQueryHandler<GetWalkQuery, WalkDto>
{
    public async Task<Result<WalkDto>> Handle(GetWalkQuery query, CancellationToken cancellationToken)
    {
        var walk = await walks.GetByIdAsync(query.WalkId, cancellationToken);
        return walk is null || walk.OwnerId != query.ViewerId
            ? WalksErrors.NotFound
            : await WalkMapper.ToDtoAsync(walk, media, cancellationToken);
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
        var rows = await walks.GetPageAsync(query.ViewerId, query.PetId, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? WalkCursor.Encode(page[^1]) : null;
        return new PagedResponse<WalkSummaryDto>(await WalkMapper.ToSummariesAsync(page, media, cancellationToken), next);
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
