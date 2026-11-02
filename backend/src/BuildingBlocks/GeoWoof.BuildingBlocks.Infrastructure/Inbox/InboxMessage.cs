using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.BuildingBlocks.Infrastructure.Inbox;

/// <summary>Records that a consumer already processed an integration event (idempotency), in the consumer's schema.</summary>
public sealed class InboxMessage
{
    /// <summary>Constructor for EF Core.</summary>
    private InboxMessage()
    {
        Consumer = string.Empty;
    }

    internal InboxMessage(Guid messageId, string consumer, DateTimeOffset processedAt)
    {
        MessageId = messageId;
        Consumer = consumer;
        ProcessedAt = processedAt;
    }

    public Guid MessageId { get; private set; }

    public string Consumer { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");
        builder.HasKey(message => new { message.MessageId, message.Consumer });
        builder.Property(message => message.Consumer).HasMaxLength(300);
    }
}
