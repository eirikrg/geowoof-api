using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Chat.Domain;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.Logging;

namespace GeoWoof.Modules.Chat.Application;

public sealed record OpenDirectConversationCommand(Guid UserId, Guid OtherUserId) : ICommand<ConversationDto>;

internal sealed class OpenDirectConversationCommandHandler(
    IChatStore store,
    IChatUnitOfWork unitOfWork,
    IIdentityApi identity,
    IRelationshipsApi relationships,
    ChatAssembler assembler,
    TimeProvider timeProvider) : ICommandHandler<OpenDirectConversationCommand, ConversationDto>
{
    public async Task<Result<ConversationDto>> Handle(OpenDirectConversationCommand command, CancellationToken cancellationToken)
    {
        if (command.UserId == command.OtherUserId)
        {
            return ChatErrors.WithYourself;
        }

        var users = await identity.GetPublicUsersAsync([command.OtherUserId], cancellationToken);
        var relationship = await relationships.GetRelationshipAsync(command.UserId, command.OtherUserId, cancellationToken);

        // A block looks like a person who does not exist, whoever made it.
        if (!users.ContainsKey(command.OtherUserId) || relationship == Relationship.Blocked)
        {
            return ChatErrors.UserNotFound;
        }

        var conversation = await store.FindConversationByPairAsync(Conversation.PairKeyOf(command.UserId, command.OtherUserId), cancellationToken);
        if (conversation is null)
        {
            if (relationship != Relationship.Friend)
            {
                return ChatErrors.NotFriends;
            }

            var now = timeProvider.GetUtcNow();
            conversation = Conversation.Start(Guid.CreateVersion7(now), command.UserId, command.OtherUserId, now);
            store.Add(conversation);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException)
            {
                // The other person opened it at the same moment.
                unitOfWork.DiscardChanges();
                return await Handle(command, cancellationToken);
            }
        }

        var row = await store.GetRowAsync(command.UserId, conversation.Id, cancellationToken);
        var built = await assembler.BuildConversationsAsync(command.UserId, [row!], cancellationToken);
        return built.Count == 0 ? ChatErrors.UserNotFound : built[0];
    }
}

public sealed record GetConversationsQuery(Guid UserId, string? Cursor, int? Limit) : IQuery<PagedResponse<ConversationDto>>;

internal sealed class GetConversationsQueryHandler(IChatStore store, IRelationshipsApi relationships, ChatAssembler assembler)
    : IQueryHandler<GetConversationsQuery, PagedResponse<ConversationDto>>
{
    public async Task<Result<PagedResponse<ConversationDto>>> Handle(GetConversationsQuery query, CancellationToken cancellationToken)
    {
        if (!ChatCursor.TryDecode(query.Cursor, out var before))
        {
            return ChatErrors.InvalidCursor;
        }

        var limit = PageLimit.Normalize(query.Limit);
        var blocked = await relationships.GetBlockedUserIdsAsync(query.UserId, cancellationToken);
        var rows = await store.GetRowsAsync(query.UserId, blocked, before, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var conversations = await assembler.BuildConversationsAsync(query.UserId, page, cancellationToken);
        return new PagedResponse<ConversationDto>(conversations, rows.Count > limit ? ChatCursor.Encode(page[^1].LastMessageId!.Value) : null);
    }
}

public sealed record GetConversationQuery(Guid UserId, Guid ConversationId) : IQuery<ConversationDto>;

internal sealed class GetConversationQueryHandler(IChatStore store, IRelationshipsApi relationships, ChatAssembler assembler)
    : IQueryHandler<GetConversationQuery, ConversationDto>
{
    public async Task<Result<ConversationDto>> Handle(GetConversationQuery query, CancellationToken cancellationToken)
    {
        var row = await store.GetRowAsync(query.UserId, query.ConversationId, cancellationToken);
        if (row is null || (await relationships.GetBlockedUserIdsAsync(query.UserId, cancellationToken)).Contains(row.OtherUserId))
        {
            return ChatErrors.ConversationNotFound;
        }

        var built = await assembler.BuildConversationsAsync(query.UserId, [row], cancellationToken);
        return built.Count == 0 ? ChatErrors.ConversationNotFound : built[0];
    }
}

public sealed record GetUnreadMessagesQuery(Guid UserId) : IQuery<CountDto>;

internal sealed class GetUnreadMessagesQueryHandler(IChatStore store, IRelationshipsApi relationships) : IQueryHandler<GetUnreadMessagesQuery, CountDto>
{
    public async Task<Result<CountDto>> Handle(GetUnreadMessagesQuery query, CancellationToken cancellationToken)
    {
        var blocked = await relationships.GetBlockedUserIdsAsync(query.UserId, cancellationToken);
        return new CountDto(await store.CountUnreadAsync(query.UserId, blocked, cancellationToken));
    }
}

public sealed record GetMessagesQuery(Guid UserId, Guid ConversationId, string? Cursor, Guid? After, int? Limit) : IQuery<PagedResponse<MessageDto>>;

internal sealed class GetMessagesQueryHandler(IChatStore store, IRelationshipsApi relationships, ChatAssembler assembler)
    : IQueryHandler<GetMessagesQuery, PagedResponse<MessageDto>>
{
    public async Task<Result<PagedResponse<MessageDto>>> Handle(GetMessagesQuery query, CancellationToken cancellationToken)
    {
        if (!ChatCursor.TryDecode(query.Cursor, out var before))
        {
            return ChatErrors.InvalidCursor;
        }

        var conversation = await store.FindConversationAsync(query.ConversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(query.UserId)
            || (await relationships.GetBlockedUserIdsAsync(query.UserId, cancellationToken)).Contains(conversation.OtherThan(query.UserId)))
        {
            return ChatErrors.ConversationNotFound;
        }

        var limit = PageLimit.Normalize(query.Limit);
        var found = query.After is { } after
            ? await store.GetMessagesAfterAsync(query.ConversationId, after, limit + 1, cancellationToken)
            : await store.GetMessagesBeforeAsync(query.ConversationId, before, limit + 1, cancellationToken);
        var page = found.Take(limit).ToList();
        var messages = await assembler.BuildMessagesAsync(page, cancellationToken);
        return new PagedResponse<MessageDto>(messages, found.Count > limit ? ChatCursor.Encode(page[^1].Id) : null);
    }
}

public sealed record SendMessageCommand(Guid UserId, Guid ConversationId, Guid ClientMessageId, string? Body, Guid? ImageAssetId) : ICommand<SentMessageDto>;

/// <summary><paramref name="IsNew"/> is <c>false</c> if it was a retry and the message was already there.</summary>
public sealed record SentMessageDto(MessageDto Message, bool IsNew);

internal sealed class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    public SendMessageCommandValidator()
    {
        RuleFor(command => command.ClientMessageId).NotEmpty();
        RuleFor(command => command.Body).MaximumLength(Message.BodyMaxLength);
        RuleFor(command => command)
            .Must(command => !string.IsNullOrWhiteSpace(command.Body) || command.ImageAssetId is not null)
            .WithName("body")
            .WithMessage("A message has some text, a photo or both.");
    }
}

internal sealed partial class SendMessageCommandHandler(
    IChatStore store,
    IChatUnitOfWork unitOfWork,
    IRelationshipsApi relationships,
    IMediaApi media,
    IChatNotifier notifier,
    ChatAssembler assembler,
    TimeProvider timeProvider,
    ILogger<SendMessageCommandHandler> logger) : ICommandHandler<SendMessageCommand, SentMessageDto>
{
    public async Task<Result<SentMessageDto>> Handle(SendMessageCommand command, CancellationToken cancellationToken)
    {
        var conversation = await store.FindConversationAsync(command.ConversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(command.UserId))
        {
            return ChatErrors.ConversationNotFound;
        }

        // Before anything else: the answer that got lost may belong to a moment when they were still friends.
        if (await ExistingAsync(command, cancellationToken) is { } retried)
        {
            return retried;
        }

        var recipientId = conversation.OtherThan(command.UserId);
        if (await relationships.GetRelationshipAsync(command.UserId, recipientId, cancellationToken) != Relationship.Friend)
        {
            return ChatErrors.NotFriends;
        }

        if (command.ImageAssetId is { } assetId
            && await media.AttachAsync(assetId, command.UserId, MediaUsage.ChatImage, cancellationToken) is { } failure)
        {
            return failure.IsNotFound ? Error.NotFound(failure.Code, failure.Message) : Error.Unprocessable(failure.Code, failure.Message);
        }

        var now = timeProvider.GetUtcNow();
        var body = string.IsNullOrWhiteSpace(command.Body) ? null : command.Body.Trim();
        var message = Message.Send(Guid.CreateVersion7(now), command.ConversationId, command.UserId, recipientId, command.ClientMessageId, body, command.ImageAssetId, now);
        store.Add(message);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // The same message arrived twice at once.
            unitOfWork.DiscardChanges();
            return await ExistingAsync(command, cancellationToken) ?? throw new InvalidOperationException("A message that conflicted cannot be found.");
        }

        var dto = (await assembler.BuildMessagesAsync([message], cancellationToken))[0];
        await TellThemAsync([command.UserId, recipientId], dto, cancellationToken);
        return new SentMessageDto(dto, IsNew: true);
    }

    private async Task<SentMessageDto?> ExistingAsync(SendMessageCommand command, CancellationToken cancellationToken)
    {
        if (await store.FindMessageAsync(command.UserId, command.ClientMessageId, cancellationToken) is not { } existing)
        {
            return null;
        }

        return new SentMessageDto((await assembler.BuildMessagesAsync([existing], cancellationToken))[0], IsNew: false);
    }

    /// <summary>The message is saved by now: if this fails, whoever is listening catches up by REST when they come back.</summary>
    private async Task TellThemAsync(IReadOnlyList<Guid> userIds, MessageDto message, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.MessageReceivedAsync(userIds, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The hub could not be told about a message")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception);
}

public sealed record MarkConversationReadCommand(Guid UserId, Guid ConversationId, Guid LastReadMessageId) : ICommand;

internal sealed partial class MarkConversationReadCommandHandler(
    IChatStore store,
    IChatUnitOfWork unitOfWork,
    IRelationshipsApi relationships,
    IChatNotifier notifier,
    ILogger<MarkConversationReadCommandHandler> logger) : ICommandHandler<MarkConversationReadCommand>
{
    public async Task<Result> Handle(MarkConversationReadCommand command, CancellationToken cancellationToken)
    {
        var conversation = await store.FindConversationAsync(command.ConversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(command.UserId))
        {
            return ChatErrors.ConversationNotFound;
        }

        var otherId = conversation.OtherThan(command.UserId);
        if ((await relationships.GetBlockedUserIdsAsync(command.UserId, cancellationToken)).Contains(otherId))
        {
            return ChatErrors.ConversationNotFound;
        }

        if (!await store.HasMessageAsync(command.ConversationId, command.LastReadMessageId, cancellationToken))
        {
            return ChatErrors.UnknownMessage;
        }

        if (!conversation.MarkRead(command.UserId, command.LastReadMessageId))
        {
            return Result.Success();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        try
        {
            var read = new ConversationReadDto(command.ConversationId, command.UserId, command.LastReadMessageId);
            await notifier.ConversationReadAsync([command.UserId, otherId], read, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(logger, exception);
        }

        return Result.Success();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The hub could not be told about a read conversation")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception);
}

public sealed record SetTypingCommand(Guid UserId, Guid ConversationId) : ICommand;

internal sealed class SetTypingCommandHandler(IChatStore store, IRelationshipsApi relationships, IChatNotifier notifier) : ICommandHandler<SetTypingCommand>
{
    public async Task<Result> Handle(SetTypingCommand command, CancellationToken cancellationToken)
    {
        var conversation = await store.FindConversationAsync(command.ConversationId, cancellationToken);
        if (conversation is null || !conversation.Includes(command.UserId))
        {
            return ChatErrors.ConversationNotFound;
        }

        var otherId = conversation.OtherThan(command.UserId);
        if ((await relationships.GetBlockedUserIdsAsync(command.UserId, cancellationToken)).Contains(otherId))
        {
            return ChatErrors.ConversationNotFound;
        }

        if (await relationships.GetRelationshipAsync(command.UserId, otherId, cancellationToken) != Relationship.Friend)
        {
            return ChatErrors.NotFriends;
        }

        await notifier.TypingChangedAsync(otherId, new TypingDto(command.ConversationId, command.UserId), cancellationToken);
        return Result.Success();
    }
}
