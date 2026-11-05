using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Follows;

/// <summary>Lets that person follow you. It does not make you follow them.</summary>
public sealed record AcceptFollowRequestCommand(Guid UserId, Guid RequestId) : ICommand<RelationshipDto>;

internal sealed class AcceptFollowRequestCommandHandler(
    IFollowRequestRepository requests,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    FollowService followService,
    RelationshipReader relationships,
    TimeProvider timeProvider) : ICommandHandler<AcceptFollowRequestCommand, RelationshipDto>
{
    public async Task<Result<RelationshipDto>> Handle(AcceptFollowRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.RequestId, cancellationToken);
        if (request is null || request.TargetId != command.UserId)
        {
            return SocialErrors.RequestNotFound;
        }

        var graph = await graphs.GetAsync(command.UserId, cancellationToken);
        if (graph.IsBlockedEitherWay(request.RequesterId))
        {
            return SocialErrors.RequestNotFound;
        }

        var accepted = request.Accept(timeProvider.GetUtcNow());
        if (accepted.IsFailure)
        {
            return accepted.Error;
        }

        await followService.StartAsync(request.RequesterId, request.TargetId, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }

        await graphs.InvalidateAsync(request.RequesterId, request.TargetId, cancellationToken);
        return await relationships.ReadAsync(command.UserId, request.RequesterId, cancellationToken);
    }
}

public sealed record RejectFollowRequestCommand(Guid UserId, Guid RequestId) : ICommand;

internal sealed class RejectFollowRequestCommandHandler(IFollowRequestRepository requests, ISocialUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<RejectFollowRequestCommand>
{
    public async Task<Result> Handle(RejectFollowRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.RequestId, cancellationToken);
        if (request is null || request.TargetId != command.UserId)
        {
            return SocialErrors.RequestNotFound;
        }

        var rejected = request.Reject(timeProvider.GetUtcNow());
        if (rejected.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return rejected;
    }
}

public sealed record CancelFollowRequestCommand(Guid UserId, Guid RequestId) : ICommand;

internal sealed class CancelFollowRequestCommandHandler(IFollowRequestRepository requests, ISocialUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<CancelFollowRequestCommand>
{
    public async Task<Result> Handle(CancelFollowRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.RequestId, cancellationToken);
        if (request is null || request.RequesterId != command.UserId)
        {
            return SocialErrors.RequestNotFound;
        }

        var cancelled = request.Cancel(timeProvider.GetUtcNow());
        if (cancelled.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return cancelled;
    }
}

public sealed record GetFollowRequestsQuery(Guid UserId, RequestDirection Direction, int? Limit, string? Cursor) : IQuery<PagedResponse<FollowRequestDto>>;

internal sealed class GetFollowRequestsQueryHandler(IFollowRequestRepository requests, ISocialGraphCache graphs, UserSummaries summaries)
    : IQueryHandler<GetFollowRequestsQuery, PagedResponse<FollowRequestDto>>
{
    public async Task<Result<PagedResponse<FollowRequestDto>>> Handle(GetFollowRequestsQuery query, CancellationToken cancellationToken)
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
        var rows = await requests.GetPendingPageAsync(query.UserId, query.Direction, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;

        // A block removes the pending requests, but one can arrive just while blocking.
        var graph = await graphs.GetAsync(query.UserId, cancellationToken);
        var people = await summaries.LoadAsync(query.UserId, page.Select(request => request.OtherThan(query.UserId)).ToList(), cancellationToken);
        var items = page
            .Where(request => people.ContainsKey(request.OtherThan(query.UserId)) && !graph.IsBlockedEitherWay(request.OtherThan(query.UserId)))
            .Select(request => new FollowRequestDto(request.Id, people[request.OtherThan(query.UserId)], query.Direction.ToString(), request.CreatedAt))
            .ToList();
        return new PagedResponse<FollowRequestDto>(items, next);
    }
}
