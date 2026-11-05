using System.Globalization;
using FluentValidation;
using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Chat.Contracts;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Notifications.Domain;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GeoWoof.Modules.Notifications.Application;

public sealed record NotificationUserDto(Guid Id, string Username, string DisplayName, string? City, MediaImage? Avatar);

public sealed record NotificationDto(
    Guid Id,
    string Type,
    NotificationUserDto Actor,
    Guid? PostId,
    Guid? CommentId,
    Guid? RequestId,
    bool IsRead,
    DateTimeOffset CreatedAt);

public sealed record CountDto(int Count);

public interface INotificationStore
{
    Task<bool> ExistsAsync(string dedupeKey, CancellationToken cancellationToken);

    /// <summary>Whether that person has asked this one to let them follow, at any time.</summary>
    Task<bool> HasRequestFromAsync(Guid recipientId, Guid actorId, CancellationToken cancellationToken);

    void Add(Notification notification);

    Task<Notification?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The most recent first.</summary>
    Task<IReadOnlyList<Notification>> GetPageAsync(Guid recipientId, IReadOnlyCollection<Guid> hiddenUserIds, Guid? before, int take, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid recipientId, IReadOnlyCollection<Guid> hiddenUserIds, CancellationToken cancellationToken);

    Task MarkAllReadAsync(Guid recipientId, DateTimeOffset now, CancellationToken cancellationToken);

    Task DeleteOfUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<DeviceToken?> FindDeviceAsync(string token, CancellationToken cancellationToken);

    void Add(DeviceToken device);

    Task<IReadOnlyList<string>> GetTokensAsync(Guid userId, CancellationToken cancellationToken);

    Task DeleteTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken cancellationToken);

    Task DeleteDeviceAsync(string token, Guid userId, CancellationToken cancellationToken);

    Task DeleteDevicesOfUserAsync(Guid userId, CancellationToken cancellationToken);
}

public interface INotificationsUnitOfWork
{
    /// <exception cref="UniqueConstraintViolationException">When a unique constraint is violated.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets what failed to save, to look again at what is really there.</summary>
    void DiscardChanges();
}

/// <summary>A message for FCM: only text, as it requires. The app builds the notification in its language.</summary>
public sealed record PushMessage(IReadOnlyDictionary<string, string> Data, bool IsHighPriority);

public interface IPushSender
{
    /// <returns>The tokens that are no longer valid, to forget them.</returns>
    Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Sends a push to someone's devices. It never fails: a push is a courtesy, and the notification
/// or the message is saved whatever happens here.
/// </summary>
internal sealed partial class PushDispatcher(INotificationStore store, IPushSender sender, ILogger<PushDispatcher> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task SendAsync(Guid userId, PushMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var tokens = await store.GetTokensAsync(userId, cancellationToken);
            if (tokens.Count == 0)
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var invalid = await sender.SendAsync(tokens, message, timeout.Token);
            if (invalid.Count > 0)
            {
                await store.DeleteTokensAsync(invalid, cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogPushFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A push could not be sent")]
    private static partial void LogPushFailed(ILogger logger, Exception exception);
}

/// <summary>Decides who is told what. Nobody hears about themselves, or about someone they have a block with, and nothing twice.</summary>
internal sealed class NotificationCenter(
    INotificationStore store,
    IRelationshipsApi relationships,
    IIdentityApi identity,
    PushDispatcher push,
    TimeProvider timeProvider)
{
    public async Task NotifyAsync(Guid recipientId, Guid actorId, NotificationType type, CancellationToken cancellationToken, Guid? postId = null, Guid? commentId = null, Guid? requestId = null)
    {
        if (recipientId == actorId
            || await relationships.GetRelationshipAsync(recipientId, actorId, cancellationToken) == Relationship.Blocked
            || await store.ExistsAsync(Notification.KeyOf(recipientId, actorId, type, postId, commentId, requestId), cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var notification = Notification.Create(Guid.CreateVersion7(now), recipientId, actorId, type, postId, commentId, requestId, now);
        store.Add(notification);

        var users = await identity.GetPublicUsersAsync([actorId], cancellationToken);
        if (!users.TryGetValue(actorId, out var actor))
        {
            return;
        }

        var seen = await SeenByAsync(recipientId, actor, cancellationToken);
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = type.ToString(),
            ["notificationId"] = notification.Id.ToString(),
            ["actorId"] = actorId.ToString(),
            ["actorName"] = seen.DisplayName,
        };
        AddIfPresent(data, "postId", postId);
        AddIfPresent(data, "commentId", commentId);
        AddIfPresent(data, "requestId", requestId);
        await push.SendAsync(recipientId, new PushMessage(data, IsHighPriority: false), cancellationToken);
    }

    private async Task<UserSummary> SeenByAsync(Guid viewerId, UserSummary user, CancellationToken cancellationToken) =>
        user.IsPrivate && !(await relationships.GetFollowingIdsAsync(viewerId, cancellationToken)).Contains(user.Id) ? user.Restricted() : user;

    private static void AddIfPresent(Dictionary<string, string> data, string key, Guid? value)
    {
        if (value is { } id)
        {
            data[key] = id.ToString();
        }
    }
}

internal sealed class NotifyOnUserFollowed(NotificationCenter center, INotificationStore store, IRelationshipsApi relationships) : IIntegrationEventHandler<UserFollowed>
{
    public async Task Handle(UserFollowed integrationEvent, CancellationToken cancellationToken)
    {
        // Becoming friends has its own notification, and whoever accepted a request already knows.
        if (await relationships.GetRelationshipAsync(integrationEvent.FolloweeId, integrationEvent.FollowerId, cancellationToken) == Relationship.Friend
            || await store.HasRequestFromAsync(integrationEvent.FolloweeId, integrationEvent.FollowerId, cancellationToken))
        {
            return;
        }

        await center.NotifyAsync(integrationEvent.FolloweeId, integrationEvent.FollowerId, NotificationType.UserFollowed, cancellationToken);
    }
}

internal sealed class NotifyOnFollowRequested(NotificationCenter center) : IIntegrationEventHandler<FollowRequested>
{
    public Task Handle(FollowRequested integrationEvent, CancellationToken cancellationToken) =>
        center.NotifyAsync(integrationEvent.TargetId, integrationEvent.RequesterId, NotificationType.FollowRequested, cancellationToken, requestId: integrationEvent.RequestId);
}

internal sealed class NotifyOnFollowRequestAccepted(NotificationCenter center) : IIntegrationEventHandler<FollowRequestAccepted>
{
    public Task Handle(FollowRequestAccepted integrationEvent, CancellationToken cancellationToken) =>
        center.NotifyAsync(integrationEvent.RequesterId, integrationEvent.TargetId, NotificationType.FollowRequestAccepted, cancellationToken, requestId: integrationEvent.RequestId);
}

/// <summary>The first of the two is the one who has just followed: only the other one has something to learn.</summary>
internal sealed class NotifyOnFriendshipStarted(NotificationCenter center) : IIntegrationEventHandler<FriendshipStarted>
{
    public Task Handle(FriendshipStarted integrationEvent, CancellationToken cancellationToken) =>
        center.NotifyAsync(integrationEvent.UserBId, integrationEvent.UserAId, NotificationType.FriendshipStarted, cancellationToken);
}

internal sealed class NotifyOnPostLiked(NotificationCenter center) : IIntegrationEventHandler<PostLiked>
{
    public Task Handle(PostLiked integrationEvent, CancellationToken cancellationToken) =>
        center.NotifyAsync(integrationEvent.PostAuthorId, integrationEvent.UserId, NotificationType.PostLiked, cancellationToken, postId: integrationEvent.PostId);
}

internal sealed class NotifyOnCommentAdded(NotificationCenter center) : IIntegrationEventHandler<CommentAdded>
{
    public Task Handle(CommentAdded integrationEvent, CancellationToken cancellationToken) =>
        center.NotifyAsync(integrationEvent.PostAuthorId, integrationEvent.AuthorId, NotificationType.CommentAdded, cancellationToken, postId: integrationEvent.PostId, commentId: integrationEvent.CommentId);
}

internal sealed class NotifyOnPostReposted(NotificationCenter center) : IIntegrationEventHandler<PostReposted>
{
    public Task Handle(PostReposted integrationEvent, CancellationToken cancellationToken) =>
        center.NotifyAsync(integrationEvent.PostAuthorId, integrationEvent.UserId, NotificationType.PostReposted, cancellationToken, postId: integrationEvent.PostId);
}

internal sealed class NotifyOnUserMentioned(NotificationCenter center) : IIntegrationEventHandler<UserMentioned>
{
    public Task Handle(UserMentioned integrationEvent, CancellationToken cancellationToken) =>
        center.NotifyAsync(integrationEvent.MentionedUserId, integrationEvent.AuthorId, NotificationType.UserMentioned, cancellationToken, postId: integrationEvent.PostId, commentId: integrationEvent.CommentId);
}

/// <summary>A message has no place in the list: it has its own counter and only needs the push.</summary>
internal sealed class PushOnMessageSent(PushDispatcher push, IIdentityApi identity, IRelationshipsApi relationships) : IIntegrationEventHandler<MessageSent>
{
    public async Task Handle(MessageSent integrationEvent, CancellationToken cancellationToken)
    {
        if (await relationships.GetRelationshipAsync(integrationEvent.RecipientId, integrationEvent.SenderId, cancellationToken) == Relationship.Blocked)
        {
            return;
        }

        var users = await identity.GetPublicUsersAsync([integrationEvent.SenderId], cancellationToken);
        if (!users.TryGetValue(integrationEvent.SenderId, out var sender))
        {
            return;
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "Message",
            ["conversationId"] = integrationEvent.ConversationId.ToString(),
            ["messageId"] = integrationEvent.MessageId.ToString(),
            ["senderId"] = integrationEvent.SenderId.ToString(),
            ["senderName"] = sender.DisplayName,
            ["body"] = integrationEvent.BodyPreview ?? string.Empty,
            ["hasImage"] = integrationEvent.HasImage.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
        };
        await push.SendAsync(integrationEvent.RecipientId, new PushMessage(data, IsHighPriority: true), cancellationToken);
    }
}

internal sealed class DeleteNotificationsOnUserDeleted(INotificationStore store) : IIntegrationEventHandler<UserDeleted>
{
    public async Task Handle(UserDeleted integrationEvent, CancellationToken cancellationToken)
    {
        await store.DeleteOfUserAsync(integrationEvent.UserId, cancellationToken);
        await store.DeleteDevicesOfUserAsync(integrationEvent.UserId, cancellationToken);
    }
}

public sealed record GetNotificationsQuery(Guid UserId, string? Cursor, int? Limit) : IQuery<PagedResponse<NotificationDto>>;

internal sealed class GetNotificationsQueryHandler(
    INotificationStore store,
    IRelationshipsApi relationships,
    IIdentityApi identity,
    IMediaApi media) : IQueryHandler<GetNotificationsQuery, PagedResponse<NotificationDto>>
{
    public async Task<Result<PagedResponse<NotificationDto>>> Handle(GetNotificationsQuery query, CancellationToken cancellationToken)
    {
        Guid? before = null;
        if (query.Cursor is not null)
        {
            if (!PageCursor.TryDecode(query.Cursor, out var value) || !Guid.TryParse(value, out var parsed))
            {
                return NotificationsErrors.InvalidCursor;
            }

            before = parsed;
        }

        var limit = PageLimit.Normalize(query.Limit);
        var hidden = await relationships.GetHiddenUserIdsAsync(query.UserId, cancellationToken);
        var found = await store.GetPageAsync(query.UserId, hidden, before, limit + 1, cancellationToken);
        var page = found.Take(limit).ToList();

        var users = await identity.GetPublicUsersAsync(page.Select(notification => notification.ActorId).Distinct().ToList(), cancellationToken);
        var following = await relationships.GetFollowingIdsAsync(query.UserId, cancellationToken);
        var images = await media.GetImagesAsync(
            users.Values.Where(user => user.AvatarAssetId is not null).Select(user => user.AvatarAssetId!.Value).ToList(),
            cancellationToken);

        var items = new List<NotificationDto>(page.Count);
        foreach (var notification in page)
        {
            if (!users.TryGetValue(notification.ActorId, out var user))
            {
                continue;
            }

            var seen = user.IsPrivate && !following.Contains(user.Id) ? user.Restricted() : user;
            items.Add(new NotificationDto(
                notification.Id,
                notification.Type.ToString(),
                new NotificationUserDto(seen.Id, seen.Username, seen.DisplayName, seen.City, seen.AvatarAssetId is { } avatar ? images.GetValueOrDefault(avatar) : null),
                notification.PostId,
                notification.CommentId,
                notification.RequestId,
                notification.ReadAt is not null,
                notification.CreatedAt));
        }

        var next = found.Count > limit ? PageCursor.Encode(page[^1].Id.ToString()) : null;
        return new PagedResponse<NotificationDto>(items, next);
    }
}

public sealed record GetUnreadNotificationsQuery(Guid UserId) : IQuery<CountDto>;

internal sealed class GetUnreadNotificationsQueryHandler(INotificationStore store, IRelationshipsApi relationships) : IQueryHandler<GetUnreadNotificationsQuery, CountDto>
{
    public async Task<Result<CountDto>> Handle(GetUnreadNotificationsQuery query, CancellationToken cancellationToken)
    {
        var hidden = await relationships.GetHiddenUserIdsAsync(query.UserId, cancellationToken);
        return new CountDto(await store.CountUnreadAsync(query.UserId, hidden, cancellationToken));
    }
}

public sealed record MarkNotificationReadCommand(Guid UserId, Guid NotificationId) : ICommand;

internal sealed class MarkNotificationReadCommandHandler(INotificationStore store, INotificationsUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<MarkNotificationReadCommand>
{
    public async Task<Result> Handle(MarkNotificationReadCommand command, CancellationToken cancellationToken)
    {
        var notification = await store.FindAsync(command.NotificationId, cancellationToken);
        if (notification is null || notification.RecipientId != command.UserId)
        {
            return NotificationsErrors.NotFound;
        }

        notification.MarkRead(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record MarkAllNotificationsReadCommand(Guid UserId) : ICommand;

internal sealed class MarkAllNotificationsReadCommandHandler(INotificationStore store, TimeProvider timeProvider) : ICommandHandler<MarkAllNotificationsReadCommand>
{
    public async Task<Result> Handle(MarkAllNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        await store.MarkAllReadAsync(command.UserId, timeProvider.GetUtcNow(), cancellationToken);
        return Result.Success();
    }
}

public sealed record RegisterDeviceCommand(Guid UserId, string Token, string Platform) : ICommand;

internal sealed class RegisterDeviceCommandValidator : AbstractValidator<RegisterDeviceCommand>
{
    public RegisterDeviceCommandValidator()
    {
        RuleFor(command => command.Token).NotEmpty().MaximumLength(DeviceToken.TokenMaxLength);
        RuleFor(command => command.Platform).Equal("Android").WithMessage("Use Android.");
    }
}

internal sealed class RegisterDeviceCommandHandler(INotificationStore store, INotificationsUnitOfWork unitOfWork, TimeProvider timeProvider) : ICommandHandler<RegisterDeviceCommand>
{
    public async Task<Result> Handle(RegisterDeviceCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (await store.FindDeviceAsync(command.Token, cancellationToken) is { } device)
        {
            device.AssignTo(command.UserId, command.Platform, now);
        }
        else
        {
            store.Add(DeviceToken.Register(Guid.CreateVersion7(now), command.Token, command.UserId, command.Platform, now));
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // The same device registered twice at once: the other attempt found no row, this one will.
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record UnregisterDeviceCommand(Guid UserId, string Token) : ICommand;

internal sealed class UnregisterDeviceCommandValidator : AbstractValidator<UnregisterDeviceCommand>
{
    public UnregisterDeviceCommandValidator()
    {
        RuleFor(command => command.Token).NotEmpty().MaximumLength(DeviceToken.TokenMaxLength);
    }
}

internal sealed class UnregisterDeviceCommandHandler(INotificationStore store) : ICommandHandler<UnregisterDeviceCommand>
{
    public async Task<Result> Handle(UnregisterDeviceCommand command, CancellationToken cancellationToken)
    {
        await store.DeleteDeviceAsync(command.Token, command.UserId, cancellationToken);
        return Result.Success();
    }
}

public static class NotificationsApplication
{
    /// <summary>Handlers and validators of the Notifications module.</summary>
    public static IServiceCollection AddNotificationsApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(NotificationsApplication).Assembly);
        services.AddScoped<NotificationCenter>();
        services.AddScoped<PushDispatcher>();
        return services;
    }
}
