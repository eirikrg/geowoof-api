using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Posts;

public sealed record AddCommentCommand(Guid PostId, Guid AuthorId, string? Text) : ICommand<CommentDto>;

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

        if (await summaries.FindAsync(command.AuthorId, command.AuthorId, cancellationToken) is not { } author)
        {
            return SocialErrors.UserNotFound;
        }

        var found = await mentions.ResolveAsync(command.Text, command.AuthorId, post.AuthorId, post.Visibility, post.AuthorIsPrivate, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var comment = Comment.Add(Guid.CreateVersion7(now), post, command.AuthorId, command.Text!, found, now);
        comments.Add(comment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CommentMapper.ToDto(comment, author, post, command.AuthorId, audience.HiddenIds);
    }
}

public sealed record DeleteCommentCommand(Guid PostId, Guid CommentId, Guid UserId) : ICommand;

internal sealed class DeleteCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork) : ICommandHandler<DeleteCommentCommand>
{
    public async Task<Result> Handle(DeleteCommentCommand command, CancellationToken cancellationToken)
    {
        var audience = PostAudience.For(command.UserId, await graphs.GetAsync(command.UserId, cancellationToken));
        if (await posts.GetVisibleAsync(command.PostId, audience, cancellationToken) is not { } post)
        {
            return SocialErrors.PostNotFound;
        }

        if (await comments.GetAsync(command.CommentId, cancellationToken) is not { } comment || comment.PostId != post.Id)
        {
            return Result.Success();
        }

        if (!comment.CanBeDeletedBy(command.UserId, post))
        {
            return SocialErrors.CannotDeleteComment;
        }

        comments.Remove(comment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

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

        var authors = await summaries.LoadAsync(query.ViewerId, page.Select(comment => comment.AuthorId).Distinct().ToList(), cancellationToken);
        var items = page
            .Where(comment => authors.ContainsKey(comment.AuthorId))
            .Select(comment => CommentMapper.ToDto(comment, authors[comment.AuthorId], post, query.ViewerId, audience.HiddenIds))
            .ToList();
        return new PagedResponse<CommentDto>(items, next);
    }
}

internal static class CommentMapper
{
    public static CommentDto ToDto(Comment comment, UserSummaryDto author, Post post, Guid viewerId, IReadOnlyCollection<Guid> hiddenIds) => new(
        comment.Id,
        comment.PostId,
        author,
        comment.Text,
        comment.Mentions
            .Where(mention => !hiddenIds.Contains(mention.UserId))
            .Select(mention => new MentionDto(mention.Username, mention.UserId))
            .ToList(),
        comment.CanBeDeletedBy(viewerId, post),
        comment.CreatedAt);
}
