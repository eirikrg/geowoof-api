using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Chat.Application;
using GeoWoof.Modules.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GeoWoof.Modules.Chat.Infrastructure.Persistence;

/// <summary>Schema <c>chat</c>: conversations and their messages.</summary>
public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : ModuleDbContext(options), IChatUnitOfWork
{
    public const string SchemaName = "chat";

    public override string Schema => SchemaName;

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationParticipant> Participants => Set<ConversationParticipant>();

    public DbSet<Message> Messages => Set<Message>();

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(builder =>
        {
            builder.ToTable("conversations");
            builder.HasKey(conversation => conversation.Id);
            builder.Property(conversation => conversation.Id).ValueGeneratedNever();
            builder.Property(conversation => conversation.PairKey).HasMaxLength(65).IsRequired();
            builder.HasIndex(conversation => conversation.PairKey).IsUnique();
            builder.HasMany(conversation => conversation.Participants)
                .WithOne()
                .HasForeignKey(participant => participant.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(conversation => conversation.Participants).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ConversationParticipant>(builder =>
        {
            builder.ToTable("conversation_participants");
            builder.HasKey(participant => new { participant.ConversationId, participant.UserId });
            builder.HasIndex(participant => participant.UserId);
        });

        modelBuilder.Entity<Message>(builder =>
        {
            builder.ToTable("messages");
            builder.HasKey(message => message.Id);
            builder.Property(message => message.Id).ValueGeneratedNever();
            builder.Property(message => message.Body).HasMaxLength(Message.BodyMaxLength);
            builder.HasOne<Conversation>().WithMany().HasForeignKey(message => message.ConversationId).OnDelete(DeleteBehavior.Cascade);

            // A retry finds the message that is already there.
            builder.HasIndex(message => new { message.SenderId, message.ClientMessageId }).IsUnique();

            // The ids grow with time: this also serves the last message and the pages of a conversation.
            builder.HasIndex(message => new { message.ConversationId, message.Id });
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations</c>.</summary>
internal sealed class ChatDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geowoof_design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ChatDbContext.SchemaName).UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ChatDbContext(options);
    }
}
