using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Chat.Domain;

/// <summary>Business errors of the chat. The codes are part of the contract (docs/api.md, phase 8).</summary>
public static class ChatErrors
{
    public static readonly Error WithYourself = Error.Validation("chat.with_yourself", "You cannot talk to yourself.");

    public static readonly Error UserNotFound = Error.NotFound("chat.user_not_found", "That person does not exist.");

    public static readonly Error ConversationNotFound = Error.NotFound("chat.conversation_not_found", "That conversation does not exist.");

    public static readonly Error NotFriends = Error.Forbidden("chat.not_friends", "Only friends can write to each other.");

    public static readonly Error UnknownMessage = Error.Validation("validation.failed", "That message is not in this conversation.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid_cursor", "The cursor is not valid.");
}

public sealed record MessageSentDomainEvent(Guid MessageId, Guid ConversationId, Guid SenderId, Guid RecipientId, string? BodyPreview, bool HasImage) : IDomainEvent;

/// <summary>Someone in a conversation, and how far they have read.</summary>
public sealed class ConversationParticipant
{
    /// <summary>Constructor for EF Core.</summary>
    private ConversationParticipant()
    {
    }

    internal ConversationParticipant(Guid conversationId, Guid userId)
    {
        ConversationId = conversationId;
        UserId = userId;
    }

    public Guid ConversationId { get; private init; }

    public Guid UserId { get; private init; }

    public Guid? LastReadMessageId { get; private set; }

    /// <returns>Whether it has moved. The ids of the messages grow with time, so an older one changes nothing.</returns>
    internal bool ReadUpTo(Guid messageId)
    {
        if (LastReadMessageId is { } current && current.CompareTo(messageId) >= 0)
        {
            return false;
        }

        LastReadMessageId = messageId;
        return true;
    }
}

/// <summary>Two people talking. There is one per pair, which <see cref="PairKey"/> guarantees.</summary>
public sealed class Conversation : AggregateRoot
{
    private readonly List<ConversationParticipant> _participants = [];

    /// <summary>Constructor for EF Core.</summary>
    private Conversation()
    {
    }

    private Conversation(Guid id, Guid firstUserId, Guid secondUserId, DateTimeOffset now)
        : base(id)
    {
        PairKey = PairKeyOf(firstUserId, secondUserId);
        CreatedAt = now;
        _participants.Add(new ConversationParticipant(id, firstUserId));
        _participants.Add(new ConversationParticipant(id, secondUserId));
    }

    public string PairKey { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<ConversationParticipant> Participants => _participants.AsReadOnly();

    /// <summary>The same whichever of the two asks.</summary>
    public static string PairKeyOf(Guid firstUserId, Guid secondUserId) =>
        firstUserId.CompareTo(secondUserId) < 0 ? $"{firstUserId:N}:{secondUserId:N}" : $"{secondUserId:N}:{firstUserId:N}";

    public static Conversation Start(Guid id, Guid firstUserId, Guid secondUserId, DateTimeOffset now) =>
        new(id, firstUserId, secondUserId, now);

    public bool Includes(Guid userId) => _participants.Exists(participant => participant.UserId == userId);

    public Guid OtherThan(Guid userId) => _participants.First(participant => participant.UserId != userId).UserId;

    /// <returns>Whether it has moved.</returns>
    public bool MarkRead(Guid userId, Guid messageId) =>
        _participants.First(participant => participant.UserId == userId).ReadUpTo(messageId);
}

/// <summary>What someone wrote. It is never edited: a retry finds the one already there by its client id.</summary>
public sealed class Message : AggregateRoot
{
    public const int BodyMaxLength = 2000;
    public const int PreviewMaxLength = 200;

    /// <summary>Constructor for EF Core.</summary>
    private Message()
    {
    }

    private Message(Guid id, Guid conversationId, Guid senderId, Guid clientMessageId, string? body, Guid? imageAssetId, DateTimeOffset now)
        : base(id)
    {
        ConversationId = conversationId;
        SenderId = senderId;
        ClientMessageId = clientMessageId;
        Body = body;
        ImageAssetId = imageAssetId;
        SentAt = now;
    }

    public Guid ConversationId { get; private set; }

    public Guid SenderId { get; private set; }

    public Guid ClientMessageId { get; private set; }

    public string? Body { get; private set; }

    public Guid? ImageAssetId { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    public static Message Send(Guid id, Guid conversationId, Guid senderId, Guid recipientId, Guid clientMessageId, string? body, Guid? imageAssetId, DateTimeOffset now)
    {
        var message = new Message(id, conversationId, senderId, clientMessageId, body, imageAssetId, now);
        var preview = body is { Length: > PreviewMaxLength } ? body[..PreviewMaxLength] : body;
        message.Raise(new MessageSentDomainEvent(id, conversationId, senderId, recipientId, preview, imageAssetId is not null));
        return message;
    }
}
