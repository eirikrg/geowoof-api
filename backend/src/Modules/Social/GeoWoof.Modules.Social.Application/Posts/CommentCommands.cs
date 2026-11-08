using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Posts;

/// <summary>With <paramref name="ParentId"/>, a reply to that comment, in its thread.</summary>
public sealed record AddCommentCommand(Guid PostId, Guid AuthorId, string? Text, Guid? ParentId = null) : ICommand<CommentDto>;

internal sealed class AddCommentCommandValidator : AbstractValidator<AddCommentCommand>
{
    public AddCommentCommandValidator()
    {
        RuleFor(command => command.Text).NotEmpty().Must(text => !string.IsNullOrWhiteSpace(text)).MaximumLength(Comment.TextMaxLength);
    }
}

internal sealed class AddCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    MentionResolver mentions,
    UserSummaries summaries,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<AddCommentCommand, CommentDto>
{
    public async Task<Result<CommentDto>> Handle(AddCommentCommand command, CancellationToken cancellationToken)
    {
        var audience = PostAudience.For(command.AuthorId, await graphs.GetAsync(command.AuthorId, cancellationToken));
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        Comment? parent = null;
        if (command.ParentId is { } parentId)
        {
            parent = await comments.GetAsync(parentId, cancellationToken);
            if (parent is null || parent.PostId != post.Id || !await CommentThreads.IsVisibleAsync(comments, parent, audience.HiddenIds, cancellationToken))
            {
                return SocialErrors.CommentNotFound;
            }
        }

        if (await summaries.FindAsync(command.AuthorId, command.AuthorId, cancellationToken) is not { } author)
        {
            return SocialErrors.UserNotFound;
        }

        var found = await mentions.ResolveAsync(command.Text, command.AuthorId, post.AuthorId, post.Visibility, post.AuthorIsPrivate, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var comment = parent is null
            ? Comment.Add(id, post, command.AuthorId, command.Text!, found, now)
            : Comment.Reply(id, post, parent, command.AuthorId, command.Text!, found, now);
        comments.Add(comment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CommentMapper.ToDto(comment, author, post, command.AuthorId, audience.HiddenIds);
    }
}

public sealed record DeleteCommentCommand(Guid PostId, Guid CommentId, Guid UserId) : ICommand;

/// <summary>
/// A comment of the post with replies stays as deleted, so that the replies of other people keep their place; without
/// them, it goes. The last reply of a deleted comment takes it with it.
/// </summary>
internal sealed class DeleteCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<DeleteCommentCommand>
{
    public async Task<Result> Handle(DeleteCommentCommand command, CancellationToken cancellationToken)
    {
        var audience = PostAudience.For(command.UserId, await graphs.GetAsync(command.UserId, cancellationToken));
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        if (await comments.GetAsync(command.CommentId, cancellationToken) is not { } comment || comment.PostId != post.Id || comment.IsDeleted)
        {
            return Result.Success();
        }

        if (!comment.CanBeDeletedBy(command.UserId, post))
        {
            return SocialErrors.CannotDeleteComment;
        }

        if (comment.ParentId is null && await comments.HasRepliesAsync(comment.Id, except: null, cancellationToken))
        {
            comment.MarkDeleted(timeProvider.GetUtcNow());
        }
        else
        {
            comments.Remove(comment);
            if (comment.ParentId is { } parentId
                && await comments.GetAsync(parentId, cancellationToken) is { IsDeleted: true } parent
                && !await comments.HasRepliesAsync(parentId, except: comment.Id, cancellationToken))
            {
                comments.Remove(parent);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>The comments of the post itself, each with the first of its replies.</summary>
public sealed record GetCommentsQuery(Guid PostId, Guid ViewerId, int? Limit, string? Cursor) : IQuery<PagedResponse<CommentDto>>;

internal sealed class GetCommentsQueryHandler(
    IPostRepository posts,
    ICommentRepository comments,
    UserSummaries summaries,
    ISocialGraphCache graphs) : IQueryHandler<GetCommentsQuery, PagedResponse<CommentDto>>
{
    public async Task<Result<PagedResponse<CommentDto>>> Handle(GetCommentsQuery query, CancellationToken cancellationToken)
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

        var audience = PostAudience.For(query.ViewerId, await graphs.GetAsync(query.ViewerId, cancellationToken));
        if (await posts.GetVisibleAsync(query.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        var limit = PageLimit.Normalize(query.Limit);
        var rows = await comments.GetPageAsync(post.Id, audience.HiddenIds, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;

        var threads = await comments.GetThreadsAsync(page.Select(comment => comment.Id).ToList(), audience.HiddenIds, CommentMapper.FirstReplies, cancellationToken);
        var authorIds = page.Select(comment => comment.AuthorId)
            .Concat(threads.Values.SelectMany(thread => thread.FirstReplies).Select(reply => reply.AuthorId))
            .Distinct()
            .ToList();
        var authors = await summaries.LoadAsync(query.ViewerId, authorIds, cancellationToken);
        var items = page
            .Where(comment => authors.ContainsKey(comment.AuthorId))
            .Select(comment => CommentMapper.ToDto(comment, authors, post, query.ViewerId, audience.HiddenIds, threads.GetValueOrDefault(comment.Id)))
            .ToList();
        return new PagedResponse<CommentDto>(items, next);
    }
}

/// <summary>All the replies of a comment of the post, page by page.</summary>
public sealed record GetRepliesQuery(Guid PostId, Guid CommentId, Guid ViewerId, int? Limit, string? Cursor) : IQuery<PagedResponse<CommentDto>>;

internal sealed class GetRepliesQueryHandler(
    IPostRepository posts,
    ICommentRepository comments,
    UserSummaries summaries,
    ISocialGraphCache graphs) : IQueryHandler<GetRepliesQuery, PagedResponse<CommentDto>>
{
    public async Task<Result<PagedResponse<CommentDto>>> Handle(GetRepliesQuery query, CancellationToken cancellationToken)
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

        var audience = PostAudience.For(query.ViewerId, await graphs.GetAsync(query.ViewerId, cancellationToken));
        if (await posts.GetVisibleAsync(query.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        if (await comments.GetAsync(query.CommentId, cancellationToken) is not { } parent
            || parent.PostId != post.Id
            || parent.ParentId is not null
            || audience.HiddenIds.Contains(parent.AuthorId))
        {
            return SocialErrors.CommentNotFound;
        }

        var limit = PageLimit.Normalize(query.Limit);
        var rows = await comments.GetRepliesPageAsync(parent.Id, audience.HiddenIds, after, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;

        var authors = await summaries.LoadAsync(query.ViewerId, page.Select(reply => reply.AuthorId).Distinct().ToList(), cancellationToken);
        var items = page
            .Where(reply => authors.ContainsKey(reply.AuthorId))
            .Select(reply => CommentMapper.ToDto(reply, authors, post, query.ViewerId, audience.HiddenIds, thread: null))
            .ToList();
        return new PagedResponse<CommentDto>(items, next);
    }
}

internal static class CommentThreads
{
    /// <summary>Whether the viewer sees it and the thread where it is: a block hides the whole thread.</summary>
    public static async Task<bool> IsVisibleAsync(ICommentRepository comments, Comment comment, IReadOnlyCollection<Guid> hiddenIds, CancellationToken cancellationToken)
    {
        if (hiddenIds.Contains(comment.AuthorId))
        {
            return false;
        }

        return comment.ParentId is not { } parentId
            || await comments.GetAsync(parentId, cancellationToken) is { } parent && !hiddenIds.Contains(parent.AuthorId);
    }
}

internal static class CommentMapper
{
    /// <summary>How many replies come with each comment of the post: the rest are asked for when they are opened.</summary>
    public const int FirstReplies = 2;

    public static CommentDto ToDto(Comment comment, UserSummaryDto author, Post post, Guid viewerId, IReadOnlyCollection<Guid> hiddenIds) =>
        ToDto(comment, author, post, viewerId, hiddenIds, replyCount: 0, replies: []);

    public static CommentDto ToDto(
        Comment comment,
        IReadOnlyDictionary<Guid, UserSummaryDto> authors,
        Post post,
        Guid viewerId,
        IReadOnlyCollection<Guid> hiddenIds,
        CommentThread? thread)
    {
        var replies = (thread?.FirstReplies ?? [])
            .Where(reply => authors.ContainsKey(reply.AuthorId))
            .Select(reply => ToDto(reply, authors[reply.AuthorId], post, viewerId, hiddenIds))
            .ToList();
        return ToDto(comment, authors[comment.AuthorId], post, viewerId, hiddenIds, thread?.ReplyCount ?? 0, replies);
    }

    private static CommentDto ToDto(
        Comment comment,
        UserSummaryDto author,
        Post post,
        Guid viewerId,
        IReadOnlyCollection<Guid> hiddenIds,
        int replyCount,
        IReadOnlyList<CommentDto> replies) => new(
        comment.Id,
        comment.PostId,
        comment.ParentId,
        author,
        comment.Text,
        comment.Mentions
            .Where(mention => !hiddenIds.Contains(mention.UserId))
            .Select(mention => new MentionDto(mention.Username, mention.UserId))
            .ToList(),
        comment.CanBeDeletedBy(viewerId, post),
        comment.IsDeleted,
        replyCount,
        replies,
        comment.CreatedAt);
}
