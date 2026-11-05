using GeoWoof.Modules.Chat.Contracts;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Notifications.Application;
using GeoWoof.Modules.Notifications.Domain;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Notifications.UnitTests;

public sealed class NotificationsTests
{
    private readonly Guid _ana = Guid.CreateVersion7();
    private readonly Guid _bea = Guid.CreateVersion7();
    private readonly InMemoryNotificationStore _store = new();
    private readonly FakeRelationships _relationships = new();
    private readonly FakeIdentity _identity = new();
    private readonly RecordingPushSender _sender = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));

    public NotificationsTests()
    {
        _identity.Add(_ana, "Ana");
        _identity.Add(_bea, "Bea");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PushDispatcher Push => new(_store, _sender, NullLogger<PushDispatcher>.Instance);

    private NotificationCenter Center => new(_store, _relationships, _identity, Push, _time);

    [Fact]
    public async Task What_someone_does_is_kept_and_goes_to_the_devices()
    {
        _store.Tokens[_ana] = ["phone"];
        var post = Guid.CreateVersion7();

        await Center.NotifyAsync(_ana, _bea, NotificationType.PostLiked, Token, postId: post);

        var saved = _store.Notifications.Single();
        saved.RecipientId.ShouldBe(_ana);
        saved.ActorId.ShouldBe(_bea);
        saved.PostId.ShouldBe(post);
        saved.ReadAt.ShouldBeNull();
        saved.CreatedAt.ShouldBe(_time.GetUtcNow());
        var (tokens, message) = _sender.Sent.Single();
        tokens.ShouldBe(["phone"]);
        message.IsHighPriority.ShouldBeFalse();
        message.Data["type"].ShouldBe("PostLiked");
        message.Data["notificationId"].ShouldBe(saved.Id.ToString());
        message.Data["actorId"].ShouldBe(_bea.ToString());
        message.Data["actorName"].ShouldBe("Bea");
        message.Data["postId"].ShouldBe(post.ToString());
        message.Data.ContainsKey("commentId").ShouldBeFalse();
        message.Data.ContainsKey("requestId").ShouldBeFalse();
    }

    [Fact]
    public async Task Nobody_hears_about_themselves()
    {
        await Center.NotifyAsync(_ana, _ana, NotificationType.PostLiked, Token, postId: Guid.CreateVersion7());

        _store.Notifications.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nobody_hears_about_someone_they_have_a_block_with_in_either_direction()
    {
        _relationships.Blocked.Add((_ana, _bea));

        await Center.NotifyAsync(_ana, _bea, NotificationType.UserFollowed, Token);
        await Center.NotifyAsync(_bea, _ana, NotificationType.UserFollowed, Token);

        _store.Notifications.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_act_does_not_tell_twice_and_a_different_one_does()
    {
        _store.Tokens[_ana] = ["phone"];
        var post = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        await Center.NotifyAsync(_ana, _bea, NotificationType.PostLiked, Token, postId: post);
        await Center.NotifyAsync(_ana, _bea, NotificationType.PostLiked, Token, postId: post);
        await Center.NotifyAsync(_ana, _bea, NotificationType.PostLiked, Token, postId: other);

        _store.Notifications.Count.ShouldBe(2);
        _sender.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Each_comment_tells_on_its_own()
    {
        var post = Guid.CreateVersion7();

        await Center.NotifyAsync(_ana, _bea, NotificationType.CommentAdded, Token, postId: post, commentId: Guid.CreateVersion7());
        await Center.NotifyAsync(_ana, _bea, NotificationType.CommentAdded, Token, postId: post, commentId: Guid.CreateVersion7());

        _store.Notifications.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_private_profile_they_do_not_follow_only_says_its_username_in_the_push()
    {
        _identity.Users[_bea] = new UserSummary(_bea, "bea_user", "Bea García", null, null, IsPrivate: true);
        _store.Tokens[_ana] = ["phone"];

        await Center.NotifyAsync(_ana, _bea, NotificationType.FollowRequested, Token, requestId: Guid.CreateVersion7());

        _sender.Sent.Single().Message.Data["actorName"].ShouldBe("bea_user");
    }

    [Fact]
    public async Task Whoever_they_follow_is_called_by_their_name()
    {
        _identity.Users[_bea] = new UserSummary(_bea, "bea_user", "Bea García", null, null, IsPrivate: true);
        _relationships.Following.Add((_ana, _bea));
        _store.Tokens[_ana] = ["phone"];

        await Center.NotifyAsync(_ana, _bea, NotificationType.PostLiked, Token, postId: Guid.CreateVersion7());

        _sender.Sent.Single().Message.Data["actorName"].ShouldBe("Bea García");
    }

    [Fact]
    public async Task Without_devices_nothing_is_sent_and_it_is_kept_anyway()
    {
        await Center.NotifyAsync(_ana, _bea, NotificationType.UserFollowed, Token);

        _store.Notifications.Count.ShouldBe(1);
        _sender.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_push_that_fails_loses_nothing()
    {
        _store.Tokens[_ana] = ["phone"];
        _sender.Fails = true;

        await Center.NotifyAsync(_ana, _bea, NotificationType.UserFollowed, Token);

        _store.Notifications.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_push_is_a_cancellation_and_not_a_lost_one()
    {
        _store.Tokens[_ana] = ["phone"];
        _sender.Fails = true;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => Push.SendAsync(_ana, new PushMessage(new Dictionary<string, string>(StringComparer.Ordinal), false), cancelled.Token));
    }

    [Fact]
    public async Task A_token_that_is_no_longer_valid_is_forgotten()
    {
        _store.Tokens[_ana] = ["good", "old"];
        _sender.Invalid.Add("old");

        await Push.SendAsync(_ana, new PushMessage(new Dictionary<string, string>(StringComparer.Ordinal), false), Token);

        _store.Tokens[_ana].ShouldBe(["good"]);
    }

    [Fact]
    public async Task Following_someone_tells_them()
    {
        await new NotifyOnUserFollowed(Center, _store, _relationships).Handle(new UserFollowed(_bea, _ana, _time.GetUtcNow()), Token);

        var saved = _store.Notifications.Single();
        saved.Type.ShouldBe(NotificationType.UserFollowed);
        saved.RecipientId.ShouldBe(_ana);
        saved.ActorId.ShouldBe(_bea);
    }

    [Fact]
    public async Task Following_back_only_says_that_they_are_friends()
    {
        _relationships.Following.Add((_ana, _bea));
        _relationships.Following.Add((_bea, _ana));
        var now = _time.GetUtcNow();

        await new NotifyOnUserFollowed(Center, _store, _relationships).Handle(new UserFollowed(_ana, _bea, now), Token);
        await new NotifyOnFriendshipStarted(Center).Handle(new FriendshipStarted(_ana, _bea, now), Token);

        // Ana has just followed back: only Bea has something to learn.
        var saved = _store.Notifications.Single();
        saved.Type.ShouldBe(NotificationType.FriendshipStarted);
        saved.RecipientId.ShouldBe(_bea);
        saved.ActorId.ShouldBe(_ana);
    }

    [Fact]
    public async Task Whoever_accepted_a_request_already_knows_they_follow_them()
    {
        var request = Guid.CreateVersion7();
        var now = _time.GetUtcNow();
        await new NotifyOnFollowRequested(Center).Handle(new FollowRequested(request, _bea, _ana, now), Token);

        await new NotifyOnUserFollowed(Center, _store, _relationships).Handle(new UserFollowed(_bea, _ana, now), Token);
        await new NotifyOnFollowRequestAccepted(Center).Handle(new FollowRequestAccepted(request, _bea, _ana, now), Token);

        _store.Notifications.Select(item => (item.RecipientId, item.Type)).ShouldBe(
            [(_ana, NotificationType.FollowRequested), (_bea, NotificationType.FollowRequestAccepted)]);
        _store.Notifications.ShouldAllBe(item => item.RequestId == request);
    }

    [Fact]
    public async Task What_is_done_with_a_post_reaches_its_author()
    {
        var post = Guid.CreateVersion7();
        var comment = Guid.CreateVersion7();
        var repost = Guid.CreateVersion7();
        var now = _time.GetUtcNow();

        await new NotifyOnPostLiked(Center).Handle(new PostLiked(post, _ana, _bea, now), Token);
        await new NotifyOnCommentAdded(Center).Handle(new CommentAdded(comment, post, _ana, _bea, now), Token);
        await new NotifyOnPostReposted(Center).Handle(new PostReposted(repost, post, _ana, _bea, now), Token);

        _store.Notifications.ShouldAllBe(item => item.RecipientId == _ana && item.ActorId == _bea && item.PostId == post);
        _store.Notifications.Select(item => item.Type).ShouldBe([NotificationType.PostLiked, NotificationType.CommentAdded, NotificationType.PostReposted]);
        _store.Notifications[1].CommentId.ShouldBe(comment);
    }

    [Fact]
    public async Task A_mention_reaches_who_is_mentioned()
    {
        var post = Guid.CreateVersion7();

        await new NotifyOnUserMentioned(Center).Handle(new UserMentioned(_bea, _ana, post, null, _time.GetUtcNow()), Token);

        var saved = _store.Notifications.Single();
        saved.Type.ShouldBe(NotificationType.UserMentioned);
        saved.RecipientId.ShouldBe(_bea);
        saved.ActorId.ShouldBe(_ana);
    }

    [Fact]
    public async Task A_message_only_goes_by_push_with_its_text_and_high_priority()
    {
        _store.Tokens[_bea] = ["phone"];
        var conversation = Guid.CreateVersion7();
        var message = Guid.CreateVersion7();

        await new PushOnMessageSent(Push, _identity, _relationships).Handle(new MessageSent(message, conversation, _ana, _bea, "Hola", HasImage: true, _time.GetUtcNow()), Token);

        _store.Notifications.ShouldBeEmpty();
        var (tokens, push) = _sender.Sent.Single();
        tokens.ShouldBe(["phone"]);
        push.IsHighPriority.ShouldBeTrue();
        push.Data["type"].ShouldBe("Message");
        push.Data["conversationId"].ShouldBe(conversation.ToString());
        push.Data["messageId"].ShouldBe(message.ToString());
        push.Data["senderId"].ShouldBe(_ana.ToString());
        push.Data["senderName"].ShouldBe("Ana");
        push.Data["body"].ShouldBe("Hola");
        push.Data["hasImage"].ShouldBe("true");
    }

    [Fact]
    public async Task A_photo_without_text_sends_an_empty_body_because_fcm_only_takes_text()
    {
        _store.Tokens[_bea] = ["phone"];

        await new PushOnMessageSent(Push, _identity, _relationships).Handle(new MessageSent(Guid.CreateVersion7(), Guid.CreateVersion7(), _ana, _bea, null, HasImage: true, _time.GetUtcNow()), Token);

        _sender.Sent.Single().Message.Data["body"].ShouldBeEmpty();
    }

    [Fact]
    public async Task No_push_arrives_from_someone_blocked()
    {
        _store.Tokens[_bea] = ["phone"];
        _relationships.Blocked.Add((_bea, _ana));

        await new PushOnMessageSent(Push, _identity, _relationships).Handle(new MessageSent(Guid.CreateVersion7(), Guid.CreateVersion7(), _ana, _bea, "Hola", false, _time.GetUtcNow()), Token);

        _sender.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deleted_account_takes_what_it_did_and_what_it_had()
    {
        await Center.NotifyAsync(_ana, _bea, NotificationType.UserFollowed, Token);
        await Center.NotifyAsync(_bea, _ana, NotificationType.UserFollowed, Token);
        _store.Tokens[_ana] = ["phone"];

        await new DeleteNotificationsOnUserDeleted(_store).Handle(new UserDeleted(_ana, _time.GetUtcNow()), Token);

        _store.Notifications.ShouldBeEmpty();
        _store.Tokens.ContainsKey(_ana).ShouldBeFalse();
    }

    [Fact]
    public async Task Reading_is_only_for_who_received_it_and_keeps_the_first_moment()
    {
        await Center.NotifyAsync(_ana, _bea, NotificationType.UserFollowed, Token);
        var saved = _store.Notifications.Single();
        var read = new MarkNotificationReadCommandHandler(_store, _store, _time);

        (await read.Handle(new MarkNotificationReadCommand(_bea, saved.Id), Token)).Error.ShouldBe(NotificationsErrors.NotFound);
        (await read.Handle(new MarkNotificationReadCommand(_ana, Guid.CreateVersion7()), Token)).Error.ShouldBe(NotificationsErrors.NotFound);

        var first = _time.GetUtcNow();
        (await read.Handle(new MarkNotificationReadCommand(_ana, saved.Id), Token)).IsSuccess.ShouldBeTrue();
        _time.Advance(TimeSpan.FromHours(1));
        await read.Handle(new MarkNotificationReadCommand(_ana, saved.Id), Token);

        saved.ReadAt.ShouldBe(first);
    }

    [Fact]
    public async Task A_device_belongs_to_the_last_account_that_registered_it()
    {
        var register = new RegisterDeviceCommandHandler(_store, _store, _time);

        await register.Handle(new RegisterDeviceCommand(_ana, "phone", "Android"), Token);
        await register.Handle(new RegisterDeviceCommand(_bea, "phone", "Android"), Token);

        _store.Devices.Single().UserId.ShouldBe(_bea);
    }

    [Theory]
    [InlineData("token", "Android", true)]
    [InlineData("", "Android", false)]
    [InlineData("token", "iOS", false)]
    public void A_device_needs_a_token_and_a_known_platform(string token, string platform, bool isValid) =>
        new RegisterDeviceCommandValidator().Validate(new RegisterDeviceCommand(_ana, token, platform)).IsValid.ShouldBe(isValid);
}

internal sealed class InMemoryNotificationStore : INotificationStore, INotificationsUnitOfWork
{
    public List<Notification> Notifications { get; } = [];

    public List<DeviceToken> Devices { get; } = [];

    public Dictionary<Guid, List<string>> Tokens { get; } = [];

    public Task<bool> ExistsAsync(string dedupeKey, CancellationToken cancellationToken) =>
        Task.FromResult(Notifications.Exists(notification => notification.DedupeKey == dedupeKey));

    public Task<bool> HasRequestFromAsync(Guid recipientId, Guid actorId, CancellationToken cancellationToken) =>
        Task.FromResult(Notifications.Exists(notification => notification.RecipientId == recipientId && notification.ActorId == actorId && notification.Type == NotificationType.FollowRequested));

    public void Add(Notification notification) => Notifications.Add(notification);

    public Task<Notification?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Notifications.FirstOrDefault(notification => notification.Id == id));

    public Task<IReadOnlyList<Notification>> GetPageAsync(Guid recipientId, IReadOnlyCollection<Guid> hiddenUserIds, Guid? before, int take, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queries are tested against PostgreSQL.");

    public Task<int> CountUnreadAsync(Guid recipientId, IReadOnlyCollection<Guid> hiddenUserIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queries are tested against PostgreSQL.");

    public Task MarkAllReadAsync(Guid recipientId, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queries are tested against PostgreSQL.");

    public Task DeleteOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        Notifications.RemoveAll(notification => notification.RecipientId == userId || notification.ActorId == userId);
        return Task.CompletedTask;
    }

    public Task<DeviceToken?> FindDeviceAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(Devices.FirstOrDefault(device => device.Token == token));

    public void Add(DeviceToken device) => Devices.Add(device);

    public Task<IReadOnlyList<string>> GetTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> found = Tokens.GetValueOrDefault(userId)?.ToList() ?? [];
        return Task.FromResult(found);
    }

    public Task DeleteTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken cancellationToken)
    {
        foreach (var list in Tokens.Values)
        {
            list.RemoveAll(tokens.Contains);
        }

        return Task.CompletedTask;
    }

    public Task DeleteDeviceAsync(string token, Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteDevicesOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        Tokens.Remove(userId);
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    public void DiscardChanges()
    {
    }
}

internal sealed class RecordingPushSender : IPushSender
{
    public bool Fails { get; set; }

    public HashSet<string> Invalid { get; } = new(StringComparer.Ordinal);

    public List<(IReadOnlyList<string> Tokens, PushMessage Message)> Sent { get; } = [];

    public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken cancellationToken)
    {
        if (Fails)
        {
            throw new InvalidOperationException("FCM is down.");
        }

        Sent.Add((tokens, message));
        IReadOnlyList<string> invalid = tokens.Where(Invalid.Contains).ToList();
        return Task.FromResult(invalid);
    }
}

internal sealed class FakeRelationships : IRelationshipsApi
{
    public HashSet<(Guid First, Guid Second)> Blocked { get; } = [];

    public HashSet<(Guid Follower, Guid Followee)> Following { get; } = [];

    public Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken)
    {
        if (Blocked.Contains((viewerId, targetId)) || Blocked.Contains((targetId, viewerId)))
        {
            return Task.FromResult(Relationship.Blocked);
        }

        if (!Following.Contains((viewerId, targetId)))
        {
            return Task.FromResult(Relationship.None);
        }

        return Task.FromResult(Following.Contains((targetId, viewerId)) ? Relationship.Friend : Relationship.Follower);
    }

    public Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([]);

    public Task<IReadOnlyCollection<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> found = Following.Where(follow => follow.Follower == userId).Select(follow => follow.Followee).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyCollection<Guid>> GetBlockedUserIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([]);
}

internal sealed class FakeIdentity : IIdentityApi
{
    public Dictionary<Guid, UserSummary> Users { get; } = [];

    public void Add(Guid userId, string name) => Users[userId] = new UserSummary(userId, name.ToUpperInvariant(), name, null, null, IsPrivate: false);

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<IReadOnlyDictionary<Guid, UserSummary>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, UserSummary> found = Users.Where(user => userIds.Contains(user.Key)).ToDictionary(user => user.Key, user => user.Value);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<string, UserSummary>> GetPublicUsersByUsernameAsync(IReadOnlyCollection<string> usernames, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, UserSummary>>(new Dictionary<string, UserSummary>(StringComparer.Ordinal));

    public Task<LiveSharingSettings?> GetLiveSharingAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<LiveSharingSettings?>(null);
}
