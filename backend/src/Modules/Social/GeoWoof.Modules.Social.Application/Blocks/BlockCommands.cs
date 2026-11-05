using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Blocks;

/// <summary>Also removes the follows, the pending requests and the reposts of each other, in both directions.</summary>
public sealed record BlockUserCommand(Guid BlockerId, Guid UserId) : ICommand;

internal sealed class BlockUserCommandHandler(
    IUserBlockRepository blocks,
    IFollowRepository follows,
    IFollowRequestRepository requests,
    IPostActionRepository postActions,
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

        await RemoveFollowsAsync(command, cancellationToken);
        await RemovePendingAsync(command.BlockerId, command.UserId, cancellationToken);
        await RemovePendingAsync(command.UserId, command.BlockerId, cancellationToken);
        foreach (var repost in await postActions.GetRepostsBetweenAsync(command.BlockerId, command.UserId, cancellationToken))
        {
            postActions.Remove(repost);
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

    /// <summary>Two rows, but one friendship: only one of them says that it ends.</summary>
    private async Task RemoveFollowsAsync(BlockUserCommand command, CancellationToken cancellationToken)
    {
        var mine = await follows.GetAsync(command.BlockerId, command.UserId, cancellationToken);
        var theirs = await follows.GetAsync(command.UserId, command.BlockerId, cancellationToken);
        if (mine is not null)
        {
            mine.End(theyFollowBack: theirs is not null);
            follows.Remove(mine);
        }

        if (theirs is not null)
        {
            theirs.End(theyFollowBack: false);
            follows.Remove(theirs);
        }
    }

    private async Task RemovePendingAsync(Guid requesterId, Guid targetId, CancellationToken cancellationToken)
    {
        if (await requests.GetPendingAsync(requesterId, targetId, cancellationToken) is { } pending)
        {
            requests.Remove(pending);
        }
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

        var people = await summaries.LoadAsync(query.UserId, page.Select(block => block.BlockedId).ToList(), cancellationToken);
        var items = page
            .Where(block => people.ContainsKey(block.BlockedId))
            .Select(block => new BlockDto(people[block.BlockedId], block.CreatedAt))
            .ToList();
        return new PagedResponse<BlockDto>(items, next);
    }
}
