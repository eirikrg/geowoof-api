using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Follows;

/// <summary>Follows at once a public profile. For a private one, it asks. Repeating it changes nothing.</summary>
public sealed record FollowUserCommand(Guid FollowerId, Guid UserId) : ICommand<RelationshipDto>;

internal sealed class FollowUserCommandHandler(
    IFollowRepository follows,
    IFollowRequestRepository requests,
    IIdentityApi identity,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    FollowService followService,
    RelationshipReader relationships,
    TimeProvider timeProvider) : ICommandHandler<FollowUserCommand, RelationshipDto>
{
    public async Task<Result<RelationshipDto>> Handle(FollowUserCommand command, CancellationToken cancellationToken)
    {
        if (command.FollowerId == command.UserId)
        {
            return SocialErrors.CannotTargetSelf;
        }

        var users = await identity.GetPublicUsersAsync([command.UserId], cancellationToken);
        var graph = await graphs.GetAsync(command.FollowerId, cancellationToken);
        if (!users.TryGetValue(command.UserId, out var target) || graph.IsBlockedEitherWay(command.UserId))
        {
            return SocialErrors.UserNotFound;
        }

        if (await follows.GetAsync(command.FollowerId, command.UserId, cancellationToken) is null)
        {
            try
            {
                if (target.IsPrivate)
                {
                    await RequestAsync(command, cancellationToken);
                }
                else
                {
                    await followService.StartAsync(command.FollowerId, command.UserId, cancellationToken);
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException)
            {
                // A concurrent request got there first: answer as if this one had arrived after it.
                unitOfWork.DiscardChanges();
                return await Handle(command, cancellationToken);
            }

            await graphs.InvalidateAsync(command.FollowerId, command.UserId, cancellationToken);
        }

        return await relationships.ReadAsync(command.FollowerId, command.UserId, cancellationToken);
    }

    private async Task RequestAsync(FollowUserCommand command, CancellationToken cancellationToken)
    {
        if (await requests.GetPendingAsync(command.FollowerId, command.UserId, cancellationToken) is null)
        {
            var now = timeProvider.GetUtcNow();
            requests.Add(FollowRequest.Send(Guid.CreateVersion7(now), command.FollowerId, command.UserId, now));
        }
    }
}

/// <summary>Stops following, or cancels the request if it had not been answered.</summary>
public sealed record UnfollowUserCommand(Guid FollowerId, Guid UserId) : ICommand<RelationshipDto>;

internal sealed class UnfollowUserCommandHandler(
    IFollowRepository follows,
    IFollowRequestRepository requests,
    IIdentityApi identity,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    FollowService followService,
    RelationshipReader relationships,
    TimeProvider timeProvider) : ICommandHandler<UnfollowUserCommand, RelationshipDto>
{
    public async Task<Result<RelationshipDto>> Handle(UnfollowUserCommand command, CancellationToken cancellationToken)
    {
        var users = await identity.GetPublicUsersAsync([command.UserId], cancellationToken);
        if (!users.ContainsKey(command.UserId))
        {
            return SocialErrors.UserNotFound;
        }

        if (await follows.GetAsync(command.FollowerId, command.UserId, cancellationToken) is { } follow)
        {
            await followService.EndAsync(follow, cancellationToken);
        }

        if (await requests.GetPendingAsync(command.FollowerId, command.UserId, cancellationToken) is { } pending)
        {
            pending.Cancel(timeProvider.GetUtcNow());
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await graphs.InvalidateAsync(command.FollowerId, command.UserId, cancellationToken);
        return await relationships.ReadAsync(command.FollowerId, command.UserId, cancellationToken);
    }
}

/// <summary>Removes someone from your followers. It ends the friendship if there was one.</summary>
public sealed record RemoveFollowerCommand(Guid UserId, Guid FollowerId) : ICommand<RelationshipDto>;

internal sealed class RemoveFollowerCommandHandler(
    IFollowRepository follows,
    IIdentityApi identity,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    FollowService followService,
    RelationshipReader relationships) : ICommandHandler<RemoveFollowerCommand, RelationshipDto>
{
    public async Task<Result<RelationshipDto>> Handle(RemoveFollowerCommand command, CancellationToken cancellationToken)
    {
        var users = await identity.GetPublicUsersAsync([command.FollowerId], cancellationToken);
        if (!users.ContainsKey(command.FollowerId))
        {
            return SocialErrors.UserNotFound;
        }

        if (await follows.GetAsync(command.FollowerId, command.UserId, cancellationToken) is { } follow)
        {
            await followService.EndAsync(follow, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await graphs.InvalidateAsync(command.FollowerId, command.UserId, cancellationToken);
        }

        return await relationships.ReadAsync(command.UserId, command.FollowerId, cancellationToken);
    }
}

public sealed record GetRelationshipQuery(Guid ViewerId, Guid UserId) : IQuery<RelationshipDto>;

internal sealed class GetRelationshipQueryHandler(IIdentityApi identity, RelationshipReader relationships)
    : IQueryHandler<GetRelationshipQuery, RelationshipDto>
{
    public async Task<Result<RelationshipDto>> Handle(GetRelationshipQuery query, CancellationToken cancellationToken)
    {
        if (query.ViewerId == query.UserId)
        {
            return await relationships.ReadAsync(query.ViewerId, query.UserId, cancellationToken);
        }

        var users = await identity.GetPublicUsersAsync([query.UserId], cancellationToken);
        return users.ContainsKey(query.UserId)
            ? await relationships.ReadAsync(query.ViewerId, query.UserId, cancellationToken)
            : SocialErrors.UserNotFound;
    }
}
