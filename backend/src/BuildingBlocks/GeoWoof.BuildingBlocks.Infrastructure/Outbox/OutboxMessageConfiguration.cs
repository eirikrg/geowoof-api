using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.BuildingBlocks.Infrastructure.Outbox;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string TableName = "outbox_messages";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.Type).HasMaxLength(200).IsRequired();
        builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(4000);
        builder.HasIndex(message => message.OccurredAt)
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("processed_at IS NULL AND failed_at IS NULL");
    }
}
