using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.Modules.Chat.Contracts;
using GeoWoof.Modules.Chat.Domain;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Chat.Application;

public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    public int MaxConnectionsPerUser { get; set; } = 5;

    /// <summary>In front of every key in Redis, so that the tests do not see each other.</summary>
    public string KeyPrefix { get; set; } = "chat:";
}

public sealed record ChatUserDto(Guid Id, string Username, string DisplayName, string? City, MediaImage? Avatar);

public sealed record MessageDto(Guid Id, Guid ConversationId, Guid SenderId, Guid ClientMessageId, string? Body, MediaImage? Image, DateTimeOffset SentAt);

public sealed record ConversationDto(
    Guid Id,
    ChatUserDto OtherUser,
    bool CanSend,
    int UnreadCount,
    MessageDto? LastMessage,
    Guid? OtherLastReadMessageId,
    DateTimeOffset UpdatedAt);

public sealed record CountDto(int Count);

public sealed record ConversationReadDto(Guid ConversationId, Guid UserId, Guid LastReadMessageId);

public sealed record TypingDto(Guid ConversationId, Guid UserId);

/// <summary>A conversation as one of the two people sees it.</summary>
public sealed record ConversationRow(Guid Id, Guid OtherUserId, Guid? LastMessageId, Guid? OtherLastReadMessageId, int UnreadCount, DateTimeOffset CreatedAt);

public sealed record MessageImageRow(Guid SenderId, Guid AssetId);

public interface IChatStore
{
    /// <summary>With its participants.</summary>
    Task<Conversation?> FindConversationAsync(Guid id, CancellationToken cancellationToken);

    Task<Conversation?> FindConversationByPairAsync(string pairKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<Conversation>> GetConversationsOfAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Conversation conversation);

    void Remove(Conversation conversation);

    Task<Message?> FindMessageAsync(Guid senderId, Guid clientMessageId, CancellationToken cancellationToken);

    Task<bool> HasMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);

    void Add(Message message);

    /// <summary>The most recent first, without those that have no messages yet or are with <paramref name="blockedUserIds"/>.</summary>
    Task<IReadOnlyList<ConversationRow>> GetRowsAsync(Guid userId, IReadOnlyCollection<Guid> blockedUserIds, Guid? before, int take, CancellationToken cancellationToken);

    Task<ConversationRow?> GetRowAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Message>> GetMessagesByIdAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>From the newest to the oldest.</summary>
    Task<IReadOnlyList<Message>> GetMessagesBeforeAsync(Guid conversationId, Guid? before, int take, CancellationToken cancellationToken);

    /// <summary>From the oldest to the newest.</summary>
    Task<IReadOnlyList<Message>> GetMessagesAfterAsync(Guid conversationId, Guid after, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<MessageImageRow>> GetImagesAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid userId, IReadOnlyCollection<Guid> blockedUserIds, CancellationToken cancellationToken);
}

public interface IChatUnitOfWork
{
    /// <exception cref="BuildingBlocks.Application.Persistence.UniqueConstraintViolationException">When a unique constraint is violated.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets what failed to save, to look again at what is really there.</summary>
    void DiscardChanges();
}

/// <summary>Reaches the people connected to the hub. The Presentation layer implements it.</summary>
public interface IChatNotifier
{
    Task MessageReceivedAsync(IReadOnlyList<Guid> userIds, MessageDto message, CancellationToken cancellationToken);

    Task ConversationReadAsync(IReadOnlyList<Guid> userIds, ConversationReadDto read, CancellationToken cancellationToken);

    Task TypingChangedAsync(Guid userId, TypingDto typing, CancellationToken cancellationToken);
}

/// <summary>How many connections to the hub each person has. It lives in Redis.</summary>
public interface IChatConnectionCounter
{
    /// <returns>Its position among the connections of that person, from the oldest and starting at 1.</returns>
    Task<long> AddAsync(Guid userId, string connectionId, CancellationToken cancellationToken);

    Task RemoveAsync(Guid userId, string connectionId, CancellationToken cancellationToken);
}

public sealed class ChatConnections(IChatConnectionCounter counter, IOptions<ChatOptions> options)
{
    /// <returns>Whether it can stay open. By position and not by count: of two opened at once, only the one that goes over closes.</returns>
    public async Task<bool> OpenAsync(Guid userId, string connectionId, CancellationToken cancellationToken) =>
        await counter.AddAsync(userId, connectionId, cancellationToken) <= options.Value.MaxConnectionsPerUser;

    public Task CloseAsync(Guid userId, string connectionId, CancellationToken cancellationToken) =>
        counter.RemoveAsync(userId, connectionId, cancellationToken);
}

internal static class ChatCursor
{
    public static string Encode(Guid id) => PageCursor.Encode(id.ToString());

    public static bool TryDecode(string? cursor, out Guid? id)
    {
        id = null;
        if (cursor is null)
        {
            return true;
        }

        if (!PageCursor.TryDecode(cursor, out var value) || !Guid.TryParse(value, out var parsed))
        {
            return false;
        }

        id = parsed;
        return true;
    }
}

/// <summary>Builds what the API answers, with the signed URLs of the images.</summary>
internal sealed class ChatAssembler(IIdentityApi identity, IRelationshipsApi relationships, IMediaApi media, IChatStore store)
{
    public async Task<IReadOnlyList<MessageDto>> BuildMessagesAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var assetIds = messages.Where(message => message.ImageAssetId is not null).Select(message => message.ImageAssetId!.Value).ToList();
        var images = await media.GetImagesAsync(assetIds, cancellationToken);
        return messages.Select(message => ToDto(message, images)).ToList();
    }

    /// <summary>An account being deleted, whose event has not arrived yet, is left out.</summary>
    public async Task<IReadOnlyList<ConversationDto>> BuildConversationsAsync(Guid viewerId, IReadOnlyList<ConversationRow> rows, CancellationToken cancellationToken)
    {
        var users = await identity.GetPublicUsersAsync(rows.Select(row => row.OtherUserId).Distinct().ToList(), cancellationToken);
        var following = await relationships.GetFollowingIdsAsync(viewerId, cancellationToken);
        var lastIds = rows.Where(row => row.LastMessageId is not null).Select(row => row.LastMessageId!.Value).ToList();
        var lastMessages = (await store.GetMessagesByIdAsync(lastIds, cancellationToken)).ToDictionary(message => message.Id);
        var assetIds = users.Values.Where(user => user.AvatarAssetId is not null).Select(user => user.AvatarAssetId!.Value)
            .Concat(lastMessages.Values.Where(message => message.ImageAssetId is not null).Select(message => message.ImageAssetId!.Value))
            .ToList();
        var images = await media.GetImagesAsync(assetIds, cancellationToken);

        var conversations = new List<ConversationDto>(rows.Count);
        foreach (var row in rows)
        {
            if (!users.TryGetValue(row.OtherUserId, out var user))
            {
                continue;
            }

            // A person is shown by the same rule everywhere: a private profile you do not follow says little.
            var seen = user.IsPrivate && !following.Contains(user.Id) ? user.Restricted() : user;
            var relationship = await relationships.GetRelationshipAsync(viewerId, row.OtherUserId, cancellationToken);
            var lastMessage = row.LastMessageId is { } lastId ? lastMessages.GetValueOrDefault(lastId) : null;
            conversations.Add(new ConversationDto(
                row.Id,
                new ChatUserDto(seen.Id, seen.Username, seen.DisplayName, seen.City, seen.AvatarAssetId is { } avatar ? images.GetValueOrDefault(avatar) : null),
                relationship == Relationship.Friend,
                row.UnreadCount,
                lastMessage is null ? null : ToDto(lastMessage, images),
                row.OtherLastReadMessageId,
                lastMessage?.SentAt ?? row.CreatedAt));
        }

        return conversations;
    }

    private static MessageDto ToDto(Message message, IReadOnlyDictionary<Guid, MediaImage> images) => new(
        message.Id,
        message.ConversationId,
        message.SenderId,
        message.ClientMessageId,
        message.Body,
        message.ImageAssetId is { } assetId ? images.GetValueOrDefault(assetId) : null,
        message.SentAt);
}

/// <summary>Publishes the public integration events of the Chat module (saved in its Outbox).</summary>
internal sealed class MessageSentHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<MessageSentDomainEvent>
{
    public Task Handle(MessageSentDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new MessageSent(
            domainEvent.MessageId,
            domainEvent.ConversationId,
            domainEvent.SenderId,
            domainEvent.RecipientId,
            domainEvent.BodyPreview,
            domainEvent.HasImage,
            timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Half a conversation would keep the words of someone who is gone, with nobody to answer them. The
/// Media module erases the photos of the deleted account on this same event; the other person's are released here.
/// </summary>
internal sealed class DeleteConversationsOnUserDeleted(IChatStore store, IMediaApi media) : IIntegrationEventHandler<UserDeleted>
{
    public async Task Handle(UserDeleted integrationEvent, CancellationToken cancellationToken)
    {
        foreach (var conversation in await store.GetConversationsOfAsync(integrationEvent.UserId, cancellationToken))
        {
            foreach (var image in await store.GetImagesAsync(conversation.Id, cancellationToken))
            {
                if (image.SenderId != integrationEvent.UserId)
                {
                    await media.ReleaseAsync(image.AssetId, image.SenderId, cancellationToken);
                }
            }

            store.Remove(conversation);
        }
    }
}

public static class ChatApplication
{
    /// <summary>Handlers and validators of the Chat module.</summary>
    public static IServiceCollection AddChatApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(ChatApplication).Assembly);
        services.AddScoped<ChatAssembler>();
        services.AddScoped<ChatConnections>();
        return services;
    }
}
