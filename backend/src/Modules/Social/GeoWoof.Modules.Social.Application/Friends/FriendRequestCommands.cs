using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Friends;

/// <summary>Accepting creates the friendship only once, even if the request is answered again.</summary>
internal sealed class FriendRequestAcceptance(IFriendshipRepository friendships, TimeProvider timeProvider)
{
    public async Task<Result> AcceptAsync(FriendRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var accepted = request.Accept(now);
        if (accepted.IsSuccess && await friendships.GetBetweenAsync(request.RequesterId, request.AddresseeId, cancellationToken) is null)
        {
            friendships.Add(Friendship.Between(Guid.CreateVersion7(now), request.RequesterId, request.AddresseeId, now));
        }

        return accepted;
    }
}

internal static class FriendRequestMapper
{
    public static FriendRequestDto ToDto(FriendRequest request, Guid viewerId, UserSummaryDto other) => new(
        request.Id,
        other,
        (request.AddresseeId == viewerId ? RequestDirection.Received : RequestDirection.Sent).ToString(),
        request.Status.ToString(),
        request.CreatedAt);
}

/// <summary>Whether this request created the friend request decides between 201 and 200.</summary>
public sealed record SendFriendRequestResult(FriendRequestDto Request, bool Created);

public sealed record SendFriendRequestCommand(Guid RequesterId, Guid UserId) : ICommand<SendFriendRequestResult>;

internal sealed class SendFriendRequestCommandValidator : AbstractValidator<SendFriendRequestCommand>
{
    public SendFriendRequestCommandValidator() => RuleFor(command => command.UserId).NotEmpty();
}

internal sealed class SendFriendRequestCommandHandler(
    IFriendRequestRepository requests,
    IFriendshipRepository friendships,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    FriendRequestAcceptance acceptance,
    UserSummaries summaries,
    TimeProvider timeProvider) : ICommandHandler<SendFriendRequestCommand, SendFriendRequestResult>
{
    public async Task<Result<SendFriendRequestResult>> Handle(SendFriendRequestCommand command, CancellationToken cancellationToken)
    {
        if (command.RequesterId == command.UserId)
        {
            return SocialErrors.CannotTargetSelf;
        }

        var other = await summaries.FindAsync(command.UserId, cancellationToken);
        var graph = await graphs.GetAsync(command.RequesterId, cancellationToken);
        if (other is null || graph.IsBlockedEitherWay(command.UserId))
        {
            return SocialErrors.UserNotFound;
        }

        if (await friendships.GetBetweenAsync(command.RequesterId, command.UserId, cancellationToken) is not null)
        {
            return SocialErrors.AlreadyFriends;
        }

        try
        {
            var pending = await requests.GetPendingBetweenAsync(command.RequesterId, command.UserId, cancellationToken);
            if (pending is null)
            {
                var now = timeProvider.GetUtcNow();
                var request = FriendRequest.Send(Guid.CreateVersion7(now), command.RequesterId, command.UserId, now);
                requests.Add(request);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return new SendFriendRequestResult(FriendRequestMapper.ToDto(request, command.RequesterId, other), Created: true);
            }

            // The other person had already asked: they both want the same.
            if (pending.AddresseeId == command.RequesterId)
            {
                await acceptance.AcceptAsync(pending, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await graphs.InvalidateAsync(command.RequesterId, command.UserId, cancellationToken);
            }

            return new SendFriendRequestResult(FriendRequestMapper.ToDto(pending, command.RequesterId, other), Created: false);
        }
        catch (UniqueConstraintViolationException)
        {
            // A concurrent request got there first: answer as if this one had arrived after it.
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }
    }
}

public sealed record AcceptFriendRequestCommand(Guid UserId, Guid RequestId) : ICommand<FriendRequestDto>;

internal sealed class AcceptFriendRequestCommandHandler(
    IFriendRequestRepository requests,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    FriendRequestAcceptance acceptance,
    UserSummaries summaries) : ICommandHandler<AcceptFriendRequestCommand, FriendRequestDto>
{
    public async Task<Result<FriendRequestDto>> Handle(AcceptFriendRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.RequestId, cancellationToken);
        if (request is null || request.AddresseeId != command.UserId)
        {
            return SocialErrors.RequestNotFound;
        }

        var graph = await graphs.GetAsync(command.UserId, cancellationToken);
        var other = await summaries.FindAsync(request.RequesterId, cancellationToken);
        if (other is null || graph.IsBlockedEitherWay(request.RequesterId))
        {
            return SocialErrors.RequestNotFound;
        }

        var accepted = await acceptance.AcceptAsync(request, cancellationToken);
        if (accepted.IsFailure)
        {
            return accepted.Error;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }

        await graphs.InvalidateAsync(request.RequesterId, request.AddresseeId, cancellationToken);
        return FriendRequestMapper.ToDto(request, command.UserId, other);
    }
}

public sealed record RejectFriendRequestCommand(Guid UserId, Guid RequestId) : ICommand;

internal sealed class RejectFriendRequestCommandHandler(IFriendRequestRepository requests, ISocialUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<RejectFriendRequestCommand>
{
    public async Task<Result> Handle(RejectFriendRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.RequestId, cancellationToken);
        if (request is null || request.AddresseeId != command.UserId)
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

public sealed record CancelFriendRequestCommand(Guid UserId, Guid RequestId) : ICommand;

internal sealed class CancelFriendRequestCommandHandler(IFriendRequestRepository requests, ISocialUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<CancelFriendRequestCommand>
{
    public async Task<Result> Handle(CancelFriendRequestCommand command, CancellationToken cancellationToken)
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

public sealed record GetFriendRequestsQuery(Guid UserId, RequestDirection Direction, int? Limit, string? Cursor) : IQuery<PagedResponse<FriendRequestDto>>;

internal sealed class GetFriendRequestsQueryHandler(IFriendRequestRepository requests, ISocialGraphCache graphs, UserSummaries summaries)
    : IQueryHandler<GetFriendRequestsQuery, PagedResponse<FriendRequestDto>>
{
    public async Task<Result<PagedResponse<FriendRequestDto>>> Handle(GetFriendRequestsQuery query, CancellationToken cancellationToken)
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
        var people = await summaries.LoadAsync(page.Select(request => request.OtherThan(query.UserId)).ToList(), cancellationToken);
        var items = page
            .Where(request => people.ContainsKey(request.OtherThan(query.UserId)) && !graph.IsBlockedEitherWay(request.OtherThan(query.UserId)))
            .Select(request => FriendRequestMapper.ToDto(request, query.UserId, people[request.OtherThan(query.UserId)]))
            .ToList();
        return new PagedResponse<FriendRequestDto>(items, next);
    }
}
