using GeoWoof.BuildingBlocks.Application;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Application.Posts;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Social.Application;

public static class SocialApplication
{
    /// <summary>Handlers and validators of the Social module, and its API for the other modules.</summary>
    public static IServiceCollection AddSocialApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(SocialApplication).Assembly);
        services.AddScoped<IRelationshipsApi, RelationshipsApi>();
        services.AddScoped<UserSummaries>();
        services.AddScoped<RelationshipReader>();
        services.AddScoped<FollowService>();
        services.AddScoped<PostAssembler>();
        services.AddScoped<MentionResolver>();
        return services;
    }
}

internal sealed class RelationshipsApi(ISocialGraphCache graphs) : IRelationshipsApi
{
    public async Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken)
    {
        if (viewerId == targetId)
        {
            return Relationship.Self;
        }

        var mine = await graphs.GetAsync(viewerId, cancellationToken);
        if (mine.IsBlockedEitherWay(targetId))
        {
            return Relationship.Blocked;
        }

        if (!mine.Following.Contains(targetId))
        {
            return Relationship.None;
        }

        var theirs = await graphs.GetAsync(targetId, cancellationToken);
        return theirs.Following.Contains(viewerId) ? Relationship.Friend : Relationship.Follower;
    }

    public async Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var graph = await graphs.GetAsync(userId, cancellationToken);
        return graph.Blocked.Union(graph.BlockedBy).ToList();
    }

    public async Task<IReadOnlyCollection<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        (await graphs.GetAsync(userId, cancellationToken)).Following;
}
