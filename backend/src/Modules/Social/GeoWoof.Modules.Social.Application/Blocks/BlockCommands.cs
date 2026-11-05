using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Blocks;

/// <summary>Also ends the friendship and removes the pending requests, in both directions.</summary>
public sealed record BlockUserCommand(Guid BlockerId, Guid UserId) : ICommand;

internal sealed class BlockUserCommandHandler(
    IUserBlockRepository blocks,
    IFriendshipRepository friendships,
    IFriendRequestRepository requests,
    IIdentityApi identity,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<BlockUserCommand>
{
    public async Task<Result> Handle(BlockUserCommand command, CancellationToken cancellationToken)
    {
        if (command.BlockerId == command.UserId)
        {
            return SocialErrors.CannotTargetSelf;
        }

        var users = await identity.GetPublicUsersAsync([command.UserId], cancellationToken);
        if (!users.ContainsKey(command.UserId))
        {
            return SocialErrors.UserNotFound;
        }

        if (await blocks.GetAsync(command.BlockerId, command.UserId, cancellationToken) is not null)
        {
            return Result.Success();
        }

        if (await friendships.GetBetweenAsync(command.BlockerId, command.UserId, cancellationToken) is { } friendship)
        {
            friendship.End();
            friendships.Remove(friendship);
        }

        if (await requests.GetPendingBetweenAsync(command.BlockerId, command.UserId, cancellationToken) is { } pending)
        {
            requests.Remove(pending);
        }

        var now = timeProvider.GetUtcNow();
        blocks.Add(UserBlock.Create(Guid.CreateVersion7(now), command.BlockerId, command.UserId, now));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }

        await graphs.InvalidateAsync(command.BlockerId, command.UserId, cancellationToken);
        return Result.Success();
    }
}

public sealed record UnblockUserCommand(Guid BlockerId, Guid UserId) : ICommand;

internal sealed class UnblockUserCommandHandler(IUserBlockRepository blocks, ISocialGraphCache graphs, ISocialUnitOfWork unitOfWork)
    : ICommandHandler<UnblockUserCommand>
{
    public async Task<Result> Handle(UnblockUserCommand command, CancellationToken cancellationToken)
    {
        if (await blocks.GetAsync(command.BlockerId, command.UserId, cancellationToken) is { } block)
        {
            blocks.Remove(block);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await graphs.InvalidateAsync(command.BlockerId, command.UserId, cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record GetBlocksQuery(Guid UserId, int? Limit, string? Cursor) : IQuery<PagedResponse<BlockDto>>;

internal sealed class GetBlocksQueryHandler(IUserBlockRepository blocks, UserSummaries summaries)
    : IQueryHandler<GetBlocksQuery, PagedResponse<BlockDto>>
{
    public async Task<Result<PagedResponse<BlockDto>>> Handle(GetBlocksQuery query, CancellationToken cancellationToken)
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
        var rows = await blocks.GetPageAsync(query.UserId, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;

        var people = await summaries.LoadAsync(page.Select(block => block.BlockedId).ToList(), cancellationToken);
        var items = page
            .Where(block => people.ContainsKey(block.BlockedId))
            .Select(block => new BlockDto(people[block.BlockedId], block.CreatedAt))
            .ToList();
        return new PagedResponse<BlockDto>(items, next);
    }
}
