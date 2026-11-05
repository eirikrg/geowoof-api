using GeoWoof.Modules.Chat.Application;
using GeoWoof.Modules.Chat.Domain;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Chat.UnitTests;

public sealed class ConversationTests
{
    private readonly Guid _ana = Guid.CreateVersion7();
    private readonly Guid _bea = Guid.CreateVersion7();

    private Conversation Conversation() => Domain.Conversation.Start(Guid.CreateVersion7(), _ana, _bea, DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_pair_is_the_same_whichever_of_the_two_asks() =>
        Domain.Conversation.PairKeyOf(_ana, _bea).ShouldBe(Domain.Conversation.PairKeyOf(_bea, _ana));

    [Fact]
    public void It_knows_who_is_in_it()
    {
        var conversation = Conversation();

        conversation.Includes(_ana).ShouldBeTrue();
        conversation.Includes(Guid.CreateVersion7()).ShouldBeFalse();
        conversation.OtherThan(_ana).ShouldBe(_bea);
        conversation.OtherThan(_bea).ShouldBe(_ana);
    }

    [Fact]
    public void Reading_only_moves_forward()
    {
        var conversation = Conversation();
        var older = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(1));
        var newer = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(2));

        conversation.MarkRead(_ana, older).ShouldBeTrue();
        conversation.MarkRead(_ana, newer).ShouldBeTrue();
        conversation.MarkRead(_ana, newer).ShouldBeFalse();
        conversation.MarkRead(_ana, older).ShouldBeFalse();
        conversation.Participants.Single(participant => participant.UserId == _ana).LastReadMessageId.ShouldBe(newer);
        conversation.Participants.Single(participant => participant.UserId == _bea).LastReadMessageId.ShouldBeNull();
    }

    [Fact]
    public void What_goes_to_the_push_is_cut_short_and_what_is_saved_is_not()
    {
        var body = new string('a', Message.BodyMaxLength);

        var message = Message.Send(Guid.CreateVersion7(), Guid.CreateVersion7(), _ana, _bea, Guid.CreateVersion7(), body, imageAssetId: null, DateTimeOffset.UnixEpoch);

        message.Body.ShouldBe(body);
        var sent = message.DomainEvents.OfType<MessageSentDomainEvent>().Single();
        sent.BodyPreview!.Length.ShouldBe(Message.PreviewMaxLength);
        sent.RecipientId.ShouldBe(_bea);
        sent.HasImage.ShouldBeFalse();
    }

    [Fact]
    public void A_photo_alone_says_it_has_one()
    {
        var message = Message.Send(Guid.CreateVersion7(), Guid.CreateVersion7(), _ana, _bea, Guid.CreateVersion7(), body: null, Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        var sent = message.DomainEvents.OfType<MessageSentDomainEvent>().Single();
        sent.BodyPreview.ShouldBeNull();
        sent.HasImage.ShouldBeTrue();
    }
}

public sealed class ChatHandlerTests
{
    private readonly Guid _ana = Guid.CreateVersion7();
    private readonly Guid _bea = Guid.CreateVersion7();
    private readonly InMemoryChatStore _store = new();
    private readonly FakeRelationships _relationships = new();
    private readonly FakeIdentity _identity = new();
    private readonly FakeMedia _media = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));

    public ChatHandlerTests()
    {
        _identity.Add(_ana, "ana");
        _identity.Add(_bea, "bea");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ChatAssembler Assembler => new(_identity, _relationships, _media, _store);

    private SendMessageCommandHandler Send => new(_store, _store, _relationships, _media, _notifier, Assembler, _time, NullLogger<SendMessageCommandHandler>.Instance);

    private MarkConversationReadCommandHandler Read => new(_store, _store, _relationships, _notifier, NullLogger<MarkConversationReadCommandHandler>.Instance);

    private SetTypingCommandHandler Typing => new(_store, _relationships, _notifier);

    private OpenDirectConversationCommandHandler Open => new(_store, _store, _identity, _relationships, Assembler, _time);

    private Conversation FriendsTalking()
    {
        _relationships.Befriend(_ana, _bea);
        var conversation = Conversation.Start(Guid.CreateVersion7(), _ana, _bea, _time.GetUtcNow());
        _store.Add(conversation);
        return conversation;
    }

    private SendMessageCommand Hello(Conversation conversation, Guid? clientMessageId = null, string? body = "Hola", Guid? imageAssetId = null) =>
        new(_ana, conversation.Id, clientMessageId ?? Guid.CreateVersion7(), body, imageAssetId);

    [Fact]
    public async Task Friends_can_open_a_conversation_and_a_second_time_it_is_the_same()
    {
        _relationships.Befriend(_ana, _bea);

        var first = await Open.Handle(new OpenDirectConversationCommand(_ana, _bea), Token);
        var second = await Open.Handle(new OpenDirectConversationCommand(_bea, _ana), Token);

        second.Value.Id.ShouldBe(first.Value.Id);
        first.Value.OtherUser.Id.ShouldBe(_bea);
        first.Value.CanSend.ShouldBeTrue();
    }

    [Fact]
    public async Task Strangers_cannot_open_one()
    {
        var result = await Open.Handle(new OpenDirectConversationCommand(_ana, _bea), Token);

        result.Error.ShouldBe(ChatErrors.NotFriends);
    }

    [Fact]
    public async Task A_person_who_does_not_exist_and_a_block_look_the_same()
    {
        _relationships.Block(_bea, _ana);

        (await Open.Handle(new OpenDirectConversationCommand(_ana, _bea), Token)).Error.ShouldBe(ChatErrors.UserNotFound);
        (await Open.Handle(new OpenDirectConversationCommand(_ana, Guid.CreateVersion7()), Token)).Error.ShouldBe(ChatErrors.UserNotFound);
    }

    [Fact]
    public async Task Nobody_talks_to_themselves()
    {
        var result = await Open.Handle(new OpenDirectConversationCommand(_ana, _ana), Token);

        result.Error.ShouldBe(ChatErrors.WithYourself);
    }

    [Fact]
    public async Task A_message_is_saved_and_told_to_both()
    {
        var conversation = FriendsTalking();

        var result = await Send.Handle(Hello(conversation, body: "  Hola  "), Token);

        result.Value.IsNew.ShouldBeTrue();
        result.Value.Message.Body.ShouldBe("Hola");
        result.Value.Message.SentAt.ShouldBe(_time.GetUtcNow());
        _store.Messages.Count.ShouldBe(1);
        var (users, told) = _notifier.Received.Single();
        users.ShouldBe([_ana, _bea]);
        told.ShouldBe(result.Value.Message);
    }

    [Fact]
    public async Task A_retry_finds_the_message_even_if_they_are_no_longer_friends()
    {
        var conversation = FriendsTalking();
        var clientMessageId = Guid.CreateVersion7();
        var first = await Send.Handle(Hello(conversation, clientMessageId), Token);
        _relationships.EndFriendship(_ana, _bea);
        _notifier.Received.Clear();

        var retry = await Send.Handle(Hello(conversation, clientMessageId), Token);

        retry.Value.IsNew.ShouldBeFalse();
        retry.Value.Message.Id.ShouldBe(first.Value.Message.Id);
        _store.Messages.Count.ShouldBe(1);
        _notifier.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_copies_at_once_end_in_one_message()
    {
        var conversation = FriendsTalking();
        var clientMessageId = Guid.CreateVersion7();
        var first = await Send.Handle(Hello(conversation, clientMessageId), Token);
        _notifier.Received.Clear();

        // As if the other copy got in between looking for it and saving this one.
        _store.HideMessageOnce = true;
        var second = await Send.Handle(Hello(conversation, clientMessageId), Token);

        second.Value.IsNew.ShouldBeFalse();
        second.Value.Message.Id.ShouldBe(first.Value.Message.Id);
        _store.Messages.Count.ShouldBe(1);
        _store.Discards.ShouldBe(1);
    }

    [Fact]
    public async Task Whoever_is_not_a_friend_cannot_write()
    {
        var conversation = FriendsTalking();
        _relationships.EndFriendship(_ana, _bea);

        var result = await Send.Handle(Hello(conversation), Token);

        result.Error.ShouldBe(ChatErrors.NotFriends);
        _store.Messages.ShouldBeEmpty();
        _notifier.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Whoever_is_not_in_it_does_not_find_it()
    {
        var conversation = FriendsTalking();

        var result = await Send.Handle(new SendMessageCommand(Guid.CreateVersion7(), conversation.Id, Guid.CreateVersion7(), "Hola", null), Token);

        result.Error.ShouldBe(ChatErrors.ConversationNotFound);
    }

    [Fact]
    public async Task A_photo_that_cannot_be_attached_says_why()
    {
        var conversation = FriendsTalking();
        _media.Failure = new MediaAttachFailure("media.not_found", "No", IsNotFound: true);
        (await Send.Handle(Hello(conversation, imageAssetId: Guid.CreateVersion7()), Token)).Error.Code.ShouldBe("media.not_found");

        _media.Failure = new MediaAttachFailure("media.not_uploaded", "No", IsNotFound: false);
        (await Send.Handle(Hello(conversation, imageAssetId: Guid.CreateVersion7()), Token)).Error.Code.ShouldBe("media.not_uploaded");

        _store.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_photo_is_attached_as_a_chat_image()
    {
        var conversation = FriendsTalking();
        var asset = Guid.CreateVersion7();

        var result = await Send.Handle(Hello(conversation, body: null, imageAssetId: asset), Token);

        _media.Attached.ShouldBe([(asset, MediaUsage.ChatImage)]);
        result.Value.Message.Image!.AssetId.ShouldBe(asset);
        result.Value.Message.Body.ShouldBeNull();
    }

    [Fact]
    public async Task If_the_hub_fails_the_message_is_saved_anyway()
    {
        var conversation = FriendsTalking();
        _notifier.Fails = true;

        var result = await Send.Handle(Hello(conversation), Token);

        result.IsSuccess.ShouldBeTrue();
        _store.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reading_tells_both_and_only_when_it_moves()
    {
        var conversation = FriendsTalking();
        var sent = await Send.Handle(Hello(conversation), Token);
        var messageId = sent.Value.Message.Id;

        (await Read.Handle(new MarkConversationReadCommand(_bea, conversation.Id, messageId), Token)).IsSuccess.ShouldBeTrue();
        (await Read.Handle(new MarkConversationReadCommand(_bea, conversation.Id, messageId), Token)).IsSuccess.ShouldBeTrue();

        _notifier.Read.Count.ShouldBe(1);
        _notifier.Read[0].Users.ShouldBe([_bea, _ana]);
        _notifier.Read[0].Read.ShouldBe(new ConversationReadDto(conversation.Id, _bea, messageId));
    }

    [Fact]
    public async Task Reading_a_message_of_another_conversation_is_refused()
    {
        var conversation = FriendsTalking();

        var result = await Read.Handle(new MarkConversationReadCommand(_bea, conversation.Id, Guid.CreateVersion7()), Token);

        result.Error.ShouldBe(ChatErrors.UnknownMessage);
    }

    [Fact]
    public async Task Whoever_blocked_does_not_find_it_to_read_or_to_type()
    {
        var conversation = FriendsTalking();
        var sent = await Send.Handle(Hello(conversation), Token);
        _relationships.Block(_bea, _ana);

        (await Read.Handle(new MarkConversationReadCommand(_bea, conversation.Id, sent.Value.Message.Id), Token)).Error.ShouldBe(ChatErrors.ConversationNotFound);
        (await Typing.Handle(new SetTypingCommand(_bea, conversation.Id), Token)).Error.ShouldBe(ChatErrors.ConversationNotFound);

        // Who was blocked sees what ending a friendship looks like.
        (await Typing.Handle(new SetTypingCommand(_ana, conversation.Id), Token)).Error.ShouldBe(ChatErrors.NotFriends);
    }

    [Fact]
    public async Task Typing_reaches_only_the_other_person()
    {
        var conversation = FriendsTalking();

        (await Typing.Handle(new SetTypingCommand(_ana, conversation.Id), Token)).IsSuccess.ShouldBeTrue();

        _notifier.Typing.ShouldBe([(_bea, new TypingDto(conversation.Id, _ana))]);
    }

    [Fact]
    public async Task A_deleted_account_takes_its_conversations_and_releases_the_photos_of_the_other()
    {
        var conversation = FriendsTalking();
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        _store.Images[conversation.Id] = [new MessageImageRow(_ana, mine), new MessageImageRow(_bea, theirs)];

        await new DeleteConversationsOnUserDeleted(_store, _media).Handle(new UserDeleted(_ana, _time.GetUtcNow()), Token);

        _store.Conversations.ShouldBeEmpty();
        _media.Released.ShouldBe([(theirs, _bea)]);
    }

    [Fact]
    public async Task Only_so_many_connections_stay_open()
    {
        var counter = new FakeConnectionCounter();
        var connections = new ChatConnections(counter, Options.Create(new ChatOptions { MaxConnectionsPerUser = 2 }));

        (await connections.OpenAsync(_ana, "1", Token)).ShouldBeTrue();
        (await connections.OpenAsync(_ana, "2", Token)).ShouldBeTrue();
        (await connections.OpenAsync(_ana, "3", Token)).ShouldBeFalse();

        await connections.CloseAsync(_ana, "3", Token);
        await connections.CloseAsync(_ana, "2", Token);
        (await connections.OpenAsync(_ana, "4", Token)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Hola", false, true)]
    [InlineData(null, true, true)]
    [InlineData("Hola", true, true)]
    [InlineData(null, false, false)]
    [InlineData("   ", false, false)]
    public void A_message_needs_text_or_a_photo(string? body, bool hasImage, bool isValid)
    {
        var command = new SendMessageCommand(_ana, Guid.CreateVersion7(), Guid.CreateVersion7(), body, hasImage ? Guid.CreateVersion7() : null);

        new SendMessageCommandValidator().Validate(command).IsValid.ShouldBe(isValid);
    }

    [Fact]
    public void A_message_has_a_limit_and_a_client_id()
    {
        var validator = new SendMessageCommandValidator();

        validator.Validate(new SendMessageCommand(_ana, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('a', Message.BodyMaxLength), null)).IsValid.ShouldBeTrue();
        validator.Validate(new SendMessageCommand(_ana, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('a', Message.BodyMaxLength + 1), null)).IsValid.ShouldBeFalse();
        validator.Validate(new SendMessageCommand(_ana, Guid.CreateVersion7(), Guid.Empty, "Hola", null)).IsValid.ShouldBeFalse();
    }
}

internal sealed class InMemoryChatStore : IChatStore, IChatUnitOfWork
{
    private readonly List<Message> _pending = [];

    public List<Conversation> Conversations { get; } = [];

    public List<Message> Messages { get; } = [];

    public Dictionary<Guid, IReadOnlyList<MessageImageRow>> Images { get; } = [];

    public bool HideMessageOnce { get; set; }

    public int Discards { get; private set; }

    public Task<Conversation?> FindConversationAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Conversations.FirstOrDefault(conversation => conversation.Id == id));

    public Task<Conversation?> FindConversationByPairAsync(string pairKey, CancellationToken cancellationToken) =>
        Task.FromResult(Conversations.FirstOrDefault(conversation => conversation.PairKey == pairKey));

    public Task<IReadOnlyList<Conversation>> GetConversationsOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Conversation> found = Conversations.Where(conversation => conversation.Includes(userId)).ToList();
        return Task.FromResult(found);
    }

    public void Add(Conversation conversation) => Conversations.Add(conversation);

    public void Remove(Conversation conversation) => Conversations.Remove(conversation);

    public Task<Message?> FindMessageAsync(Guid senderId, Guid clientMessageId, CancellationToken cancellationToken)
    {
        if (HideMessageOnce)
        {
            HideMessageOnce = false;
            return Task.FromResult<Message?>(null);
        }

        return Task.FromResult(Messages.FirstOrDefault(message => message.SenderId == senderId && message.ClientMessageId == clientMessageId));
    }

    public Task<bool> HasMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) =>
        Task.FromResult(Messages.Exists(message => message.ConversationId == conversationId && message.Id == messageId));

    public void Add(Message message) => _pending.Add(message);

    public Task<IReadOnlyList<ConversationRow>> GetRowsAsync(Guid userId, IReadOnlyCollection<Guid> blockedUserIds, Guid? before, int take, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queries are tested against PostgreSQL.");

    public Task<ConversationRow?> GetRowAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = Conversations.First(found => found.Id == conversationId);
        return Task.FromResult<ConversationRow?>(new ConversationRow(conversation.Id, conversation.OtherThan(userId), null, null, 0, conversation.CreatedAt));
    }

    public Task<IReadOnlyList<Message>> GetMessagesByIdAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        IReadOnlyList<Message> found = Messages.Where(message => ids.Contains(message.Id)).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Message>> GetMessagesBeforeAsync(Guid conversationId, Guid? before, int take, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queries are tested against PostgreSQL.");

    public Task<IReadOnlyList<Message>> GetMessagesAfterAsync(Guid conversationId, Guid after, int take, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queries are tested against PostgreSQL.");

    public Task<IReadOnlyList<MessageImageRow>> GetImagesAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Images.GetValueOrDefault(conversationId) ?? []);

    public Task<int> CountUnreadAsync(Guid userId, IReadOnlyCollection<Guid> blockedUserIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queries are tested against PostgreSQL.");

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var message in _pending)
        {
            if (Messages.Exists(existing => existing.SenderId == message.SenderId && existing.ClientMessageId == message.ClientMessageId))
            {
                throw new BuildingBlocks.Application.Persistence.UniqueConstraintViolationException("messages", "The same message twice.", new InvalidOperationException());
            }

            Messages.Add(message);
        }

        var saved = _pending.Count;
        _pending.Clear();
        return Task.FromResult(saved);
    }

    public void DiscardChanges()
    {
        Discards++;
        _pending.Clear();
    }
}

internal sealed class FakeRelationships : IRelationshipsApi
{
    private readonly HashSet<(Guid Follower, Guid Followee)> _follows = [];
    private readonly HashSet<(Guid Blocker, Guid Blocked)> _blocks = [];

    public void Befriend(Guid first, Guid second)
    {
        _follows.Add((first, second));
        _follows.Add((second, first));
    }

    public void EndFriendship(Guid first, Guid second)
    {
        _follows.Remove((first, second));
        _follows.Remove((second, first));
    }

    public void Block(Guid blocker, Guid blocked)
    {
        _blocks.Add((blocker, blocked));
        EndFriendship(blocker, blocked);
    }

    public Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken)
    {
        if (viewerId == targetId)
        {
            return Task.FromResult(Relationship.Self);
        }

        if (_blocks.Contains((viewerId, targetId)) || _blocks.Contains((targetId, viewerId)))
        {
            return Task.FromResult(Relationship.Blocked);
        }

        if (!_follows.Contains((viewerId, targetId)))
        {
            return Task.FromResult(Relationship.None);
        }

        return Task.FromResult(_follows.Contains((targetId, viewerId)) ? Relationship.Friend : Relationship.Follower);
    }

    public Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([]);

    public Task<IReadOnlyCollection<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> found = _follows.Where(follow => follow.Follower == userId).Select(follow => follow.Followee).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyCollection<Guid>> GetBlockedUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> found = _blocks.Where(block => block.Blocker == userId).Select(block => block.Blocked).ToList();
        return Task.FromResult(found);
    }
}

internal sealed class FakeIdentity : IIdentityApi
{
    private readonly Dictionary<Guid, UserSummary> _users = [];

    public void Add(Guid userId, string username) => _users[userId] = new UserSummary(userId, username, username, null, null, IsPrivate: false);

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<IReadOnlyDictionary<Guid, UserSummary>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, UserSummary> found = _users.Where(user => userIds.Contains(user.Key)).ToDictionary(user => user.Key, user => user.Value);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<string, UserSummary>> GetPublicUsersByUsernameAsync(IReadOnlyCollection<string> usernames, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, UserSummary>>(new Dictionary<string, UserSummary>(StringComparer.Ordinal));

    public Task<LiveSharingSettings?> GetLiveSharingAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<LiveSharingSettings?>(null);
}

internal sealed class FakeMedia : IMediaApi
{
    public MediaAttachFailure? Failure { get; set; }

    public List<(Guid AssetId, MediaUsage Usage)> Attached { get; } = [];

    public List<(Guid AssetId, Guid OwnerId)> Released { get; } = [];

    public Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken)
    {
        if (Failure is null)
        {
            Attached.Add((assetId, usage));
        }

        return Task.FromResult(Failure);
    }

    public Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken)
    {
        Released.Add((assetId, ownerId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, MediaImage> found = assetIds.ToDictionary(id => id, id => new MediaImage(id, $"https://thumb/{id}", $"https://medium/{id}"));
        return Task.FromResult(found);
    }

    public Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class RecordingNotifier : IChatNotifier
{
    public bool Fails { get; set; }

    public List<(IReadOnlyList<Guid> Users, MessageDto Message)> Received { get; } = [];

    public List<(IReadOnlyList<Guid> Users, ConversationReadDto Read)> Read { get; } = [];

    public List<(Guid User, TypingDto Typing)> Typing { get; } = [];

    public Task MessageReceivedAsync(IReadOnlyList<Guid> userIds, MessageDto message, CancellationToken cancellationToken)
    {
        if (Fails)
        {
            throw new InvalidOperationException("The backplane is down.");
        }

        Received.Add((userIds, message));
        return Task.CompletedTask;
    }

    public Task ConversationReadAsync(IReadOnlyList<Guid> userIds, ConversationReadDto read, CancellationToken cancellationToken)
    {
        Read.Add((userIds, read));
        return Task.CompletedTask;
    }

    public Task TypingChangedAsync(Guid userId, TypingDto typing, CancellationToken cancellationToken)
    {
        Typing.Add((userId, typing));
        return Task.CompletedTask;
    }
}

internal sealed class FakeConnectionCounter : IChatConnectionCounter
{
    private readonly Dictionary<Guid, List<string>> _connections = [];

    public Task<long> AddAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(userId, out var connections))
        {
            connections = [];
            _connections[userId] = connections;
        }

        connections.Add(connectionId);
        return Task.FromResult((long)connections.Count);
    }

    public Task RemoveAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        _connections.GetValueOrDefault(userId)?.Remove(connectionId);
        return Task.CompletedTask;
    }
}
