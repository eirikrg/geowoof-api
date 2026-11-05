using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Domain;
using Microsoft.EntityFrameworkCore;

namespace GeoWoof.Modules.Social.Infrastructure.Persistence;

/// <summary>Relationships between the sample accounts (docs/api.md, phase 5). Idempotent.</summary>
internal sealed class SocialDevelopmentSeeder(SocialDbContext dbContext, IIdentityApi identity, TimeProvider timeProvider) : IDevelopmentDataSeeder
{
    public int Order => 30;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var ana = await identity.FindUserIdByEmailAsync("ana@geowoof.dev", cancellationToken);
        var bea = await identity.FindUserIdByEmailAsync("bea@geowoof.dev", cancellationToken);
        var carlos = await identity.FindUserIdByEmailAsync("carlos@geowoof.dev", cancellationToken);
        var dani = await identity.FindUserIdByEmailAsync("dani@geowoof.dev", cancellationToken);
        if (ana is not { } anaId || bea is not { } beaId || carlos is not { } carlosId || dani is not { } daniId)
        {
            return;
        }

        // Only once: restarting the API must not bring back a follow that someone removed while trying the app.
        if (await dbContext.Follows.AnyAsync(cancellationToken) || await dbContext.FollowRequests.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        dbContext.Follows.Add(Follow.Start(Guid.CreateVersion7(now), anaId, beaId, theyFollowBack: false, now));
        dbContext.Follows.Add(Follow.Start(Guid.CreateVersion7(now), beaId, anaId, theyFollowBack: true, now));
        dbContext.Follows.Add(Follow.Start(Guid.CreateVersion7(now), carlosId, anaId, theyFollowBack: false, now));
        dbContext.FollowRequests.Add(FollowRequest.Send(Guid.CreateVersion7(now), beaId, daniId, now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
