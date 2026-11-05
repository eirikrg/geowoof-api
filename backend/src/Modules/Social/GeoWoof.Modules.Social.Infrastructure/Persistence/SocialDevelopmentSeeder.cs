using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Social.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace GeoWoof.Modules.Social.Infrastructure.Persistence;

/// <summary>Relationships and posts of the sample accounts (docs/api.md, phases 5 and 6). Idempotent.</summary>
internal sealed partial class SocialDevelopmentSeeder(
    SocialDbContext dbContext,
    IIdentityApi identity,
    IMediaApi media,
    IPetsApi pets,
    TimeProvider timeProvider,
    ILogger<SocialDevelopmentSeeder> logger) : IDevelopmentDataSeeder
{
    private const int PhotoSize = 1200;

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

        await SeedRelationshipsAsync(anaId, beaId, carlosId, daniId, cancellationToken);
        try
        {
            await SeedPostsAsync(anaId, beaId, carlosId, daniId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sample data must never stop the API from starting: without storage, there are no sample posts.
            dbContext.ChangeTracker.Clear();
            LogPostsFailed(logger, exception);
        }
    }

    private async Task SeedRelationshipsAsync(Guid anaId, Guid beaId, Guid carlosId, Guid daniId, CancellationToken cancellationToken)
    {
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

    private async Task SeedPostsAsync(Guid anaId, Guid beaId, Guid carlosId, Guid daniId, CancellationToken cancellationToken)
    {
        // Only once, too: whoever tries the app may have deleted them.
        if (await dbContext.Posts.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var anaWalk = await AddPostAsync(
            anaId,
            "Mañana de sol en el Retiro con @bea",
            ContentVisibility.Everyone,
            [new SKColor(0xF2, 0x8C, 0x38), new SKColor(0xE0, 0x6C, 0x4C)],
            [new Mention(beaId, "bea")],
            now.AddHours(-30),
            cancellationToken);
        await AddPostAsync(anaId, "Esta foto es solo para mis amigos", ContentVisibility.Friends, [new SKColor(0x4C, 0x8B, 0xB0)], [], now.AddHours(-20), cancellationToken);
        var beaPost = await AddPostAsync(beaId, "Mishi ha encontrado el mejor sitio de la casa", ContentVisibility.Everyone, [new SKColor(0x7A, 0x9E, 0x5F)], [], now.AddHours(-12), cancellationToken);
        var carlosPost = await AddPostAsync(carlosId, "Rocky no se cansa nunca", ContentVisibility.Everyone, [new SKColor(0xA8, 0x6A, 0xB8)], [], now.AddHours(-6), cancellationToken);
        await AddPostAsync(daniId, "Bimba vigilando", ContentVisibility.Everyone, [new SKColor(0x5A, 0x7F, 0xC4)], [], now.AddHours(-2), cancellationToken);

        dbContext.PostLikes.Add(PostLike.Create(Guid.CreateVersion7(now), anaWalk, beaId, now.AddHours(-29)));
        dbContext.PostLikes.Add(PostLike.Create(Guid.CreateVersion7(now), anaWalk, carlosId, now.AddHours(-28)));
        dbContext.PostLikes.Add(PostLike.Create(Guid.CreateVersion7(now), beaPost, anaId, now.AddHours(-11)));
        dbContext.PostLikes.Add(PostLike.Create(Guid.CreateVersion7(now), carlosPost, anaId, now.AddHours(-5)));
        dbContext.Comments.Add(Comment.Add(Guid.CreateVersion7(now), anaWalk, carlosId, "Qué bonito está el parque, @ana", [new Mention(anaId, "ana")], now.AddHours(-27)));
        dbContext.Comments.Add(Comment.Add(Guid.CreateVersion7(now), anaWalk, beaId, "¡Repetimos la semana que viene!", [], now.AddHours(-26)));
        dbContext.Reposts.Add(Repost.Create(Guid.CreateVersion7(now), anaWalk, beaId, now.AddHours(-25)));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Post> AddPostAsync(
        Guid authorId,
        string text,
        ContentVisibility visibility,
        IReadOnlyList<SKColor> colors,
        IReadOnlyCollection<Mention> mentions,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var assetIds = new List<Guid>();
        foreach (var color in colors)
        {
            var assetId = await media.ImportAsync(authorId, MediaUsage.PostImage, DrawPhoto(color), "image/png", cancellationToken);
            await media.AttachAsync(assetId, authorId, MediaUsage.PostImage, cancellationToken);
            assetIds.Add(assetId);
        }

        var petIds = await pets.GetPetIdsAsync(authorId, cancellationToken);
        var authors = await identity.GetPublicUsersAsync([authorId], cancellationToken);
        var post = Post.Create(Guid.CreateVersion7(at), authorId, text, visibility, authors[authorId].IsPrivate, assetIds, petIds.Take(1).ToList(), mentions, at);
        dbContext.Posts.Add(post);
        return post;
    }

    /// <summary>A plain placeholder photo: a coloured background with a paw print.</summary>
    private static byte[] DrawPhoto(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(PhotoSize, PhotoSize));
        var canvas = surface.Canvas;
        canvas.Clear(color);

        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(120), IsAntialias = true };
        var center = PhotoSize / 2f;
        canvas.DrawOval(center, center + 140, 240, 200, paint);
        canvas.DrawOval(center - 290, center - 60, 85, 120, paint);
        canvas.DrawOval(center - 105, center - 220, 85, 130, paint);
        canvas.DrawOval(center + 105, center - 220, 85, 130, paint);
        canvas.DrawOval(center + 290, center - 60, 85, 120, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sample posts could not be created")]
    private static partial void LogPostsFailed(ILogger logger, Exception exception);
}
