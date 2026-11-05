using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Posts;

/// <summary>Whether this request created the post decides between 201 and 200.</summary>
public sealed record PutPostResult(PostDto Post, bool Created);

public sealed record PutPostCommand(
    Guid PostId,
    Guid AuthorId,
    string? Text,
    string? Visibility,
    IReadOnlyList<Guid>? MediaAssetIds,
    IReadOnlyList<Guid>? PetIds) : ICommand<PutPostResult>;

internal sealed class PutPostCommandValidator : AbstractValidator<PutPostCommand>
{
    public PutPostCommandValidator()
    {
        RuleFor(command => command.PostId).NotEmpty();
        RuleFor(command => command.Text).MaximumLength(Post.TextMaxLength);
        RuleFor(command => command.Visibility)
            .Must(visibility => visibility is nameof(ContentVisibility.Everyone) or nameof(ContentVisibility.Friends))
            .WithMessage("It must be Everyone or Friends.");
        RuleFor(command => command.MediaAssetIds)
            .NotNull()
            .Must(ids => ids is { Count: > 0 and <= Post.MaxMedia } && ids.Distinct().Count() == ids.Count)
            .WithMessage($"From 1 to {Post.MaxMedia} images, without repeating any.");
        RuleFor(command => command.PetIds)
            .Must(ids => ids is null || (ids.Count <= Post.MaxPets && ids.Distinct().Count() == ids.Count))
            .WithMessage($"At most {Post.MaxPets} pets, without repeating any.");
    }
}

internal sealed class PutPostCommandHandler(
    IPostRepository posts,
    PostAssembler assembler,
    MentionResolver mentions,
    IIdentityApi identity,
    IPetsApi pets,
    IMediaApi media,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<PutPostCommand, PutPostResult>
{
    public async Task<Result<PutPostResult>> Handle(PutPostCommand command, CancellationToken cancellationToken)
    {
        if (await posts.GetAsync(command.PostId, cancellationToken) is { } existing)
        {
            return existing.AuthorId == command.AuthorId
                ? await ExistingAsync(command, cancellationToken)
                : SocialErrors.PostIdTaken;
        }

        var users = await identity.GetPublicUsersAsync([command.AuthorId], cancellationToken);
        if (!users.TryGetValue(command.AuthorId, out var author))
        {
            return SocialErrors.UserNotFound;
        }

        var petIds = command.PetIds ?? [];
        var owned = await pets.GetPetIdsAsync(command.AuthorId, cancellationToken);
        if (!petIds.All(owned.Contains))
        {
            return SocialErrors.PetNotOwned;
        }

        var assetIds = command.MediaAssetIds!;
        foreach (var assetId in assetIds)
        {
            if (await media.AttachAsync(assetId, command.AuthorId, MediaUsage.PostImage, cancellationToken) is { } failure)
            {
                return failure.IsNotFound ? Error.NotFound(failure.Code, failure.Message) : Error.Unprocessable(failure.Code, failure.Message);
            }
        }

        var visibility = Enum.Parse<ContentVisibility>(command.Visibility!);
        var found = await mentions.ResolveAsync(command.Text, command.AuthorId, command.AuthorId, visibility, author.IsPrivate, cancellationToken);
        posts.Add(Post.Create(command.PostId, command.AuthorId, command.Text, visibility, author.IsPrivate, assetIds, petIds, found, timeProvider.GetUtcNow()));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // The same request arrived twice at the same moment: the other one created it.
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }

        var post = await assembler.FindAsync(command.AuthorId, command.PostId, cancellationToken);
        return post is null ? SocialErrors.PostNotFound : new PutPostResult(post, Created: true);
    }

    private async Task<Result<PutPostResult>> ExistingAsync(PutPostCommand command, CancellationToken cancellationToken)
    {
        var post = await assembler.FindAsync(command.AuthorId, command.PostId, cancellationToken);
        return post is null ? SocialErrors.PostNotFound : new PutPostResult(post, Created: false);
    }
}

public sealed record DeletePostCommand(Guid PostId, Guid UserId) : ICommand;

internal sealed class DeletePostCommandHandler(
    IPostRepository posts,
    ISocialGraphCache graphs,
    IMediaApi media,
    ISocialUnitOfWork unitOfWork) : ICommandHandler<DeletePostCommand>
{
    public async Task<Result> Handle(DeletePostCommand command, CancellationToken cancellationToken)
    {
        var audience = PostAudience.For(command.UserId, await graphs.GetAsync(command.UserId, cancellationToken));

        // What cannot be seen does not exist for this person, not even to say that it is not theirs.
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is null
            || await posts.GetAsync(command.PostId, cancellationToken) is not { } post)
        {
            return Result.Success();
        }

        if (post.AuthorId != command.UserId)
        {
            return SocialErrors.NotPostAuthor;
        }

        var assetIds = post.Media.Select(item => item.AssetId).ToList();
        posts.Remove(post);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var assetId in assetIds)
        {
            await media.ReleaseAsync(assetId, command.UserId, cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record LikePostCommand(Guid PostId, Guid UserId) : ICommand<PostLikeDto>;

internal sealed class LikePostCommandHandler(
    IPostRepository posts,
    IPostActionRepository actions,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<LikePostCommand, PostLikeDto>
{
    public async Task<Result<PostLikeDto>> Handle(LikePostCommand command, CancellationToken cancellationToken)
    {
        var graph = await graphs.GetAsync(command.UserId, cancellationToken);
        var audience = PostAudience.For(command.UserId, graph);
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        if (await actions.GetLikeAsync(post.Id, command.UserId, cancellationToken) is null)
        {
            var now = timeProvider.GetUtcNow();
            actions.Add(PostLike.Create(Guid.CreateVersion7(now), post, command.UserId, now));
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException)
            {
                // Two taps at the same moment: the other one already counted it.
                unitOfWork.DiscardChanges();
            }
        }

        var stats = await posts.GetStatsAsync([post.Id], command.UserId, audience.HiddenIds, cancellationToken);
        return new PostLikeDto(stats.GetValueOrDefault(post.Id)?.Likes ?? 0, LikedByMe: true);
    }
}

public sealed record UnlikePostCommand(Guid PostId, Guid UserId) : ICommand<PostLikeDto>;

internal sealed class UnlikePostCommandHandler(
    IPostRepository posts,
    IPostActionRepository actions,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork) : ICommandHandler<UnlikePostCommand, PostLikeDto>
{
    public async Task<Result<PostLikeDto>> Handle(UnlikePostCommand command, CancellationToken cancellationToken)
    {
        var audience = PostAudience.For(command.UserId, await graphs.GetAsync(command.UserId, cancellationToken));
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        if (await actions.GetLikeAsync(post.Id, command.UserId, cancellationToken) is { } like)
        {
            actions.Remove(like);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var stats = await posts.GetStatsAsync([post.Id], command.UserId, audience.HiddenIds, cancellationToken);
        return new PostLikeDto(stats.GetValueOrDefault(post.Id)?.Likes ?? 0, LikedByMe: false);
    }
}

public sealed record RepostCommand(Guid PostId, Guid UserId) : ICommand<PostRepostDto>;

internal sealed class RepostCommandHandler(
    IPostRepository posts,
    IPostActionRepository actions,
    IFollowRepository follows,
    IIdentityApi identity,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RepostCommand, PostRepostDto>
{
    public async Task<Result<PostRepostDto>> Handle(RepostCommand command, CancellationToken cancellationToken)
    {
        var graph = await graphs.GetAsync(command.UserId, cancellationToken);
        var audience = PostAudience.For(command.UserId, graph);
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        var authors = await identity.GetPublicUsersAsync([post.AuthorId], cancellationToken);
        if (!authors.TryGetValue(post.AuthorId, out var author))
        {
            return SocialErrors.PostNotFound;
        }

        var authorFollowsViewer = await follows.GetAsync(post.AuthorId, command.UserId, cancellationToken) is not null;
        var relation = ViewerRelations.Of(command.UserId, post.AuthorId, graph, authorFollowsViewer);
        if (!VisibilityRules.CanRepost(relation, post.Visibility, author.IsPrivate))
        {
            return SocialErrors.CannotRepost;
        }

        if (await actions.GetRepostAsync(post.Id, command.UserId, cancellationToken) is null)
        {
            var now = timeProvider.GetUtcNow();
            actions.Add(Repost.Create(Guid.CreateVersion7(now), post, command.UserId, now));
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException)
            {
                // Two taps at the same moment: the other one already did it.
                unitOfWork.DiscardChanges();
            }
        }

        var stats = await posts.GetStatsAsync([post.Id], command.UserId, audience.HiddenIds, cancellationToken);
        return new PostRepostDto(stats.GetValueOrDefault(post.Id)?.Reposts ?? 0, RepostedByMe: true);
    }
}

public sealed record UndoRepostCommand(Guid PostId, Guid UserId) : ICommand<PostRepostDto>;

internal sealed class UndoRepostCommandHandler(
    IPostRepository posts,
    IPostActionRepository actions,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork) : ICommandHandler<UndoRepostCommand, PostRepostDto>
{
    public async Task<Result<PostRepostDto>> Handle(UndoRepostCommand command, CancellationToken cancellationToken)
    {
        // Before anything else: it has to be possible even if the original can no longer be seen.
        if (await actions.GetRepostAsync(command.PostId, command.UserId, cancellationToken) is { } repost)
        {
            actions.Remove(repost);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var audience = PostAudience.For(command.UserId, await graphs.GetAsync(command.UserId, cancellationToken));
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is null)
        {
            return SocialErrors.PostNotFound;
        }

        var stats = await posts.GetStatsAsync([command.PostId], command.UserId, audience.HiddenIds, cancellationToken);
        return new PostRepostDto(stats.GetValueOrDefault(command.PostId)?.Reposts ?? 0, RepostedByMe: false);
    }
}

public sealed record SavePostCommand(Guid PostId, Guid UserId) : ICommand;

internal sealed class SavePostCommandHandler(
    IPostRepository posts,
    IPostActionRepository actions,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<SavePostCommand>
{
    public async Task<Result> Handle(SavePostCommand command, CancellationToken cancellationToken)
    {
        var audience = PostAudience.For(command.UserId, await graphs.GetAsync(command.UserId, cancellationToken));
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is null)
        {
            return SocialErrors.PostNotFound;
        }

        if (await actions.GetSavedAsync(command.PostId, command.UserId, cancellationToken) is null)
        {
            var now = timeProvider.GetUtcNow();
            actions.Add(SavedPost.Create(Guid.CreateVersion7(now), command.PostId, command.UserId, now));
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException)
            {
                // Two taps at the same moment: the other one already saved it.
                unitOfWork.DiscardChanges();
            }
        }

        return Result.Success();
    }
}

/// <summary>It does not ask whether the post can still be seen: it has to be possible to take it out of the list.</summary>
public sealed record UnsavePostCommand(Guid PostId, Guid UserId) : ICommand;

internal sealed class UnsavePostCommandHandler(IPostActionRepository actions, ISocialUnitOfWork unitOfWork) : ICommandHandler<UnsavePostCommand>
{
    public async Task<Result> Handle(UnsavePostCommand command, CancellationToken cancellationToken)
    {
        if (await actions.GetSavedAsync(command.PostId, command.UserId, cancellationToken) is { } saved)
        {
            actions.Remove(saved);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
