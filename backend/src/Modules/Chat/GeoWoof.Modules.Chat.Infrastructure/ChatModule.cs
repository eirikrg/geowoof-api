using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Chat.Application;
using GeoWoof.Modules.Chat.Contracts;
using GeoWoof.Modules.Chat.Domain;
using GeoWoof.Modules.Chat.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GeoWoof.Modules.Chat.Infrastructure;

public static class ChatModule
{
    public static IServiceCollection AddChatModule(this IServiceCollection services)
    {
        services.AddChatApplication();
        services.AddOptions<ChatOptions>().BindConfiguration(ChatOptions.SectionName);
        services.AddModuleDbContext<ChatDbContext>(ChatDbContext.SchemaName);
        services.AddScoped<IChatUnitOfWork>(provider => provider.GetRequiredService<ChatDbContext>());
        services.AddScoped<IChatStore, ChatStore>();
        services.AddSingleton<IChatConnectionCounter, RedisChatConnectionCounter>();
        services.AddScoped<IDevelopmentDataSeeder, ChatDevelopmentSeeder>();
        services.AddIntegrationEvents(typeof(MessageSent).Assembly);
        services.AddIntegrationEventConsumers<ChatDbContext>(typeof(ChatApplication).Assembly);
        return services;
    }
}

/// <summary>A connection closes when its token expires, so one older than the limit was left behind by a server that stopped.</summary>
internal sealed class RedisChatConnectionCounter(IConnectionMultiplexer redis, IOptions<ChatOptions> options, TimeProvider timeProvider) : IChatConnectionCounter
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public async Task<long> AddAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        RedisKey key = Key(userId);
        var now = timeProvider.GetUtcNow();
        await database.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, (now - Lifetime).ToUnixTimeMilliseconds());
        await database.SortedSetAddAsync(key, connectionId, now.ToUnixTimeMilliseconds());
        await database.KeyExpireAsync(key, Lifetime);
        return (await database.SortedSetRankAsync(key, connectionId) ?? 0) + 1;
    }

    public Task RemoveAsync(Guid userId, string connectionId, CancellationToken cancellationToken) =>
        redis.GetDatabase().SortedSetRemoveAsync(Key(userId), connectionId);

    private string Key(Guid userId) => $"{options.Value.KeyPrefix}conns:{userId}";
}

/// <summary>Two people talking, from the point of view of one of them.</summary>
internal sealed class ChatStore(ChatDbContext dbContext) : IChatStore
{
    public Task<Conversation?> FindConversationAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Conversations.Include(conversation => conversation.Participants)
            .FirstOrDefaultAsync(conversation => conversation.Id == id, cancellationToken);

    public Task<Conversation?> FindConversationByPairAsync(string pairKey, CancellationToken cancellationToken) =>
        dbContext.Conversations.Include(conversation => conversation.Participants)
            .FirstOrDefaultAsync(conversation => conversation.PairKey == pairKey, cancellationToken);

    public async Task<IReadOnlyList<Conversation>> GetConversationsOfAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Conversations.Include(conversation => conversation.Participants)
            .Where(conversation => conversation.Participants.Any(participant => participant.UserId == userId))
            .ToListAsync(cancellationToken);

    public void Add(Conversation conversation) => dbContext.Conversations.Add(conversation);

    public void Remove(Conversation conversation) => dbContext.Conversations.Remove(conversation);

    public Task<Message?> FindMessageAsync(Guid senderId, Guid clientMessageId, CancellationToken cancellationToken) =>
        dbContext.Messages.FirstOrDefaultAsync(message => message.SenderId == senderId && message.ClientMessageId == clientMessageId, cancellationToken);

    public Task<bool> HasMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.Messages.AnyAsync(message => message.ConversationId == conversationId && message.Id == messageId, cancellationToken);

    public void Add(Message message) => dbContext.Messages.Add(message);

    public async Task<IReadOnlyList<ConversationRow>> GetRowsAsync(Guid userId, IReadOnlyCollection<Guid> blockedUserIds, Guid? before, int take, CancellationToken cancellationToken)
    {
        var blocked = blockedUserIds.ToList();
        var rows = Rows(userId).Where(row => row.LastMessageId != null && !blocked.Contains(row.OtherUserId));
        if (before is { } position)
        {
            rows = rows.Where(row => row.LastMessageId!.Value.CompareTo(position) < 0);
        }

        var found = await rows.OrderByDescending(row => row.LastMessageId).Take(take).ToListAsync(cancellationToken);
        return found.Select(row => row.ToRow()).ToList();
    }

    public async Task<ConversationRow?> GetRowAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken) =>
        (await Rows(userId).FirstOrDefaultAsync(row => row.Id == conversationId, cancellationToken))?.ToRow();

    public async Task<IReadOnlyList<Message>> GetMessagesByIdAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToList();
        return await dbContext.Messages.Where(message => wanted.Contains(message.Id)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetMessagesBeforeAsync(Guid conversationId, Guid? before, int take, CancellationToken cancellationToken)
    {
        var messages = dbContext.Messages.Where(message => message.ConversationId == conversationId);
        if (before is { } position)
        {
            messages = messages.Where(message => message.Id.CompareTo(position) < 0);
        }

        return await messages.OrderByDescending(message => message.Id).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetMessagesAfterAsync(Guid conversationId, Guid after, int take, CancellationToken cancellationToken) =>
        await dbContext.Messages
            .Where(message => message.ConversationId == conversationId && message.Id.CompareTo(after) > 0)
            .OrderBy(message => message.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MessageImageRow>> GetImagesAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await dbContext.Messages
            .Where(message => message.ConversationId == conversationId && message.ImageAssetId != null)
            .Select(message => new MessageImageRow(message.SenderId, message.ImageAssetId!.Value))
            .ToListAsync(cancellationToken);

    public async Task<int> CountUnreadAsync(Guid userId, IReadOnlyCollection<Guid> blockedUserIds, CancellationToken cancellationToken)
    {
        var blocked = blockedUserIds.ToList();
        return await Rows(userId).Where(row => !blocked.Contains(row.OtherUserId)).SumAsync(row => row.UnreadCount, cancellationToken);
    }

    /// <summary>The last message and the unread ones are counted when asked: there is no second truth to keep in step.</summary>
    private IQueryable<RowData> Rows(Guid userId) =>
        from mine in dbContext.Participants
        where mine.UserId == userId
        join other in dbContext.Participants on mine.ConversationId equals other.ConversationId
        where other.UserId != userId
        join conversation in dbContext.Conversations on mine.ConversationId equals conversation.Id
        select new RowData
        {
            Id = conversation.Id,
            OtherUserId = other.UserId,
            LastMessageId = dbContext.Messages.Where(message => message.ConversationId == conversation.Id)
                .OrderByDescending(message => message.Id)
                .Select(message => (Guid?)message.Id)
                .FirstOrDefault(),
            OtherLastReadMessageId = other.LastReadMessageId,
            UnreadCount = dbContext.Messages.Count(message => message.ConversationId == conversation.Id
                && message.SenderId != userId
                && (mine.LastReadMessageId == null || message.Id.CompareTo(mine.LastReadMessageId.Value) > 0)),
            CreatedAt = conversation.CreatedAt,
        };

    /// <summary>EF Core cannot filter or sort by a member of a positional record, so the record is built at the end.</summary>
    private sealed class RowData
    {
        public Guid Id { get; init; }

        public Guid OtherUserId { get; init; }

        public Guid? LastMessageId { get; init; }

        public Guid? OtherLastReadMessageId { get; init; }

        public int UnreadCount { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public ConversationRow ToRow() => new(Id, OtherUserId, LastMessageId, OtherLastReadMessageId, UnreadCount, CreatedAt);
    }
}

/// <summary>A conversation between the sample accounts Ana and Bea. Idempotent.</summary>
internal sealed class ChatDevelopmentSeeder(ChatDbContext dbContext, IIdentityApi identity, TimeProvider timeProvider) : IDevelopmentDataSeeder
{
    public int Order => 40;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var ana = await identity.FindUserIdByEmailAsync("ana@geowoof.dev", cancellationToken);
        var bea = await identity.FindUserIdByEmailAsync("bea@geowoof.dev", cancellationToken);
        if (ana is not { } anaId || bea is not { } beaId || await dbContext.Conversations.AnyAsync(cancellationToken))
        {
            return;
        }

        var start = timeProvider.GetUtcNow().AddMinutes(-30);
        var conversation = Conversation.Start(Guid.CreateVersion7(start), anaId, beaId, start);
        dbContext.Conversations.Add(conversation);
        var lines = new (Guid Sender, string Body)[]
        {
            (beaId, "¿Salimos esta tarde con los perros?"),
            (anaId, "¡Sí! ¿A las 7 en el parque?"),
            (beaId, "Perfecto, Mishi ya está nerviosa."),
        };
        for (var index = 0; index < lines.Length; index++)
        {
            var sentAt = start.AddMinutes(index + 1);
            var (sender, body) = lines[index];
            var recipient = sender == anaId ? beaId : anaId;
            var message = Message.Send(Guid.CreateVersion7(sentAt), conversation.Id, sender, recipient, Guid.CreateVersion7(sentAt), body, null, sentAt);

            // Sample data does not tell anyone.
            message.ClearDomainEvents();
            dbContext.Messages.Add(message);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
