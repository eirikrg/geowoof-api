using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Domain;
using Microsoft.EntityFrameworkCore;

namespace GeoWoof.Modules.Social.Infrastructure.Persistence;

/// <summary>Relationships between the sample accounts (docs/api.md, phase 4). Idempotent.</summary>
internal sealed class SocialDevelopmentSeeder(SocialDbContext dbContext, IIdentityApi identity, TimeProvider timeProvider) : IDevelopmentDataSeeder
{
    public int Order => 30;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var ana = await identity.FindUserIdByEmailAsync("ana@geowoof.dev", cancellationToken);
        var bea = await identity.FindUserIdByEmailAsync("bea@geowoof.dev", cancellationToken);
        var carlos = await identity.FindUserIdByEmailAsync("carlos@geowoof.dev", cancellationToken);
        if (ana is not { } anaId || bea is not { } beaId || carlos is not { } carlosId)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var friends = new UserPair(anaId, beaId);
        if (!await dbContext.Friendships.AnyAsync(friendship => friendship.UserAId == friends.Low && friendship.UserBId == friends.High, cancellationToken))
        {
            dbContext.Friendships.Add(Friendship.Between(Guid.CreateVersion7(now), anaId, beaId, now));
        }

        if (!await dbContext.FriendRequests.AnyAsync(request => request.RequesterId == carlosId && request.AddresseeId == anaId, cancellationToken))
        {
            dbContext.FriendRequests.Add(FriendRequest.Send(Guid.CreateVersion7(now), carlosId, anaId, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
