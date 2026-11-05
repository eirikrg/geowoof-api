using GeoWoof.BuildingBlocks.Application;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Application.Friends;
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
        services.AddScoped<FriendRequestAcceptance>();
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

        var graph = await graphs.GetAsync(viewerId, cancellationToken);
        if (graph.IsBlockedEitherWay(targetId))
        {
            return Relationship.Blocked;
        }

        return graph.Friends.Contains(targetId) ? Relationship.Friend : Relationship.None;
    }

    public async Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var graph = await graphs.GetAsync(userId, cancellationToken);
        return graph.Blocked.Union(graph.BlockedBy).ToList();
    }
}
