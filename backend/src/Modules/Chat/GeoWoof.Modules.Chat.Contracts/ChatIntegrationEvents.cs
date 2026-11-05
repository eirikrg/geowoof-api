using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.Modules.Chat.Contracts;

/// <summary>A message has been sent. <paramref name="BodyPreview"/> is cut short: it is only for the push.</summary>
[EventType("chat.message_sent.v1")]
public sealed record MessageSent(
    Guid MessageId,
    Guid ConversationId,
    Guid SenderId,
    Guid RecipientId,
    string? BodyPreview,
    bool HasImage,
    DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
