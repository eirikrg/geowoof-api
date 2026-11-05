using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Notifications.Application;
using GeoWoof.Modules.Notifications.Domain;
using GeoWoof.Modules.Notifications.Infrastructure.Persistence;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DomainNotification = GeoWoof.Modules.Notifications.Domain.Notification;

namespace GeoWoof.Modules.Notifications.Infrastructure;

public sealed class FirebaseOptions
{
    public const string SectionName = "Firebase";

    /// <summary>The service account key, outside the repository. Without it, what would be sent goes to the log (ADR-0012).</summary>
    public string? CredentialsPath { get; set; }
}

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddNotificationsApplication();
        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.SchemaName);
        services.AddScoped<INotificationsUnitOfWork>(provider => provider.GetRequiredService<NotificationsDbContext>());
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddScoped<IDevelopmentDataSeeder, NotificationsDevelopmentSeeder>();
        services.AddIntegrationEventConsumers<NotificationsDbContext>(typeof(NotificationsApplication).Assembly);
        services.AddSingleton<IPushSender>(provider =>
        {
            var path = provider.GetRequiredService<IConfiguration>().GetSection(FirebaseOptions.SectionName).Get<FirebaseOptions>()?.CredentialsPath;
            var loggers = provider.GetRequiredService<ILoggerFactory>();
            return string.IsNullOrWhiteSpace(path)
                ? new LoggingPushSender(loggers.CreateLogger<LoggingPushSender>())
                : new FirebasePushSender(path, loggers.CreateLogger<FirebasePushSender>());
        });
        return services;
    }
}

/// <summary>Without Firebase, everything else works and the log says what would have been sent. Never the text of a message.</summary>
internal sealed partial class LoggingPushSender(ILogger<LoggingPushSender> logger) : IPushSender
{
    public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken cancellationToken)
    {
        var type = message.Data.GetValueOrDefault("type") ?? "unknown";
        LogPush(logger, type, tokens.Count, message.IsHighPriority);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A push of type {Type} would go to {Devices} devices (high priority: {IsHighPriority})")]
    private static partial void LogPush(ILogger logger, string type, int devices, bool isHighPriority);
}

/// <summary>Real pushes. A key that cannot be read only stops them: it must not stop the API from starting.</summary>
internal sealed partial class FirebasePushSender : IPushSender
{
    private const string AppName = "geowoof";

    private readonly ILogger<FirebasePushSender> _logger;
    private readonly FirebaseMessaging? _messaging;

    public FirebasePushSender(string credentialsPath, ILogger<FirebasePushSender> logger)
    {
        _logger = logger;
        try
        {
            var app = FirebaseApp.GetInstance(AppName) ?? FirebaseApp.Create(
                new AppOptions { Credential = CredentialFactory.FromFile<ServiceAccountCredential>(credentialsPath).ToGoogleCredential() },
                AppName);
            _messaging = FirebaseMessaging.GetMessaging(app);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the type: the message of a failure reading a key could quote what is inside it.
            LogKeyUnreadable(_logger, exception.GetType().Name);
        }
    }

    public async Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken cancellationToken)
    {
        if (_messaging is null)
        {
            return [];
        }

        var multicast = new MulticastMessage
        {
            Fids = tokens,
            Data = message.Data,
            Android = new AndroidConfig { Priority = message.IsHighPriority ? Priority.High : Priority.Normal },
        };
        var answer = await _messaging.SendEachForMulticastAsync(multicast, cancellationToken);
        var invalid = new List<string>();
        for (var index = 0; index < answer.Responses.Count; index++)
        {
            if (answer.Responses[index].Exception?.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument or MessagingErrorCode.SenderIdMismatch)
            {
                invalid.Add(tokens[index]);
            }
        }

        return invalid;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The Firebase key could not be read ({Reason}): no push will be sent")]
    private static partial void LogKeyUnreadable(ILogger logger, string reason);
}

internal sealed class NotificationStore(NotificationsDbContext dbContext) : INotificationStore
{
    public Task<bool> ExistsAsync(string dedupeKey, CancellationToken cancellationToken) =>
        dbContext.Notifications.AnyAsync(notification => notification.DedupeKey == dedupeKey, cancellationToken);

    public Task<bool> HasRequestFromAsync(Guid recipientId, Guid actorId, CancellationToken cancellationToken) =>
        dbContext.Notifications.AnyAsync(
            notification => notification.RecipientId == recipientId && notification.ActorId == actorId && notification.Type == NotificationType.FollowRequested,
            cancellationToken);

    public void Add(DomainNotification notification) => dbContext.Notifications.Add(notification);

    public Task<DomainNotification?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Notifications.FirstOrDefaultAsync(notification => notification.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DomainNotification>> GetPageAsync(Guid recipientId, IReadOnlyCollection<Guid> hiddenUserIds, Guid? before, int take, CancellationToken cancellationToken)
    {
        var hidden = hiddenUserIds.ToList();
        var notifications = dbContext.Notifications.Where(notification => notification.RecipientId == recipientId && !hidden.Contains(notification.ActorId));
        if (before is { } position)
        {
            notifications = notifications.Where(notification => notification.Id.CompareTo(position) < 0);
        }

        return await notifications.OrderByDescending(notification => notification.Id).Take(take).ToListAsync(cancellationToken);
    }

    public Task<int> CountUnreadAsync(Guid recipientId, IReadOnlyCollection<Guid> hiddenUserIds, CancellationToken cancellationToken)
    {
        var hidden = hiddenUserIds.ToList();
        return dbContext.Notifications.CountAsync(
            notification => notification.RecipientId == recipientId && notification.ReadAt == null && !hidden.Contains(notification.ActorId),
            cancellationToken);
    }

    public Task MarkAllReadAsync(Guid recipientId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Notifications
            .Where(notification => notification.RecipientId == recipientId && notification.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAt, now), cancellationToken);

    public Task DeleteOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Notifications
            .Where(notification => notification.RecipientId == userId || notification.ActorId == userId)
            .ExecuteDeleteAsync(cancellationToken);

    public Task<DeviceToken?> FindDeviceAsync(string token, CancellationToken cancellationToken) =>
        dbContext.Devices.FirstOrDefaultAsync(device => device.Token == token, cancellationToken);

    public void Add(DeviceToken device) => dbContext.Devices.Add(device);

    public async Task<IReadOnlyList<string>> GetTokensAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Devices.Where(device => device.UserId == userId).Select(device => device.Token).ToListAsync(cancellationToken);

    public Task DeleteTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken cancellationToken)
    {
        var wanted = tokens.ToList();
        return dbContext.Devices.Where(device => wanted.Contains(device.Token)).ExecuteDeleteAsync(cancellationToken);
    }

    public Task DeleteDeviceAsync(string token, Guid userId, CancellationToken cancellationToken) =>
        dbContext.Devices.Where(device => device.Token == token && device.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    public Task DeleteDevicesOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Devices.Where(device => device.UserId == userId).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>Notifications for the sample account Ana (docs/api.md, phase 8). Idempotent.</summary>
internal sealed class NotificationsDevelopmentSeeder(NotificationsDbContext dbContext, IIdentityApi identity, TimeProvider timeProvider) : IDevelopmentDataSeeder
{
    public int Order => 50;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var ana = await identity.FindUserIdByEmailAsync("ana@geowoof.dev", cancellationToken);
        var bea = await identity.FindUserIdByEmailAsync("bea@geowoof.dev", cancellationToken);
        var carlos = await identity.FindUserIdByEmailAsync("carlos@geowoof.dev", cancellationToken);
        if (ana is not { } anaId || bea is not { } beaId || carlos is not { } carlosId || await dbContext.Notifications.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var friendship = now.AddHours(-3);
        var follow = now.AddMinutes(-40);
        dbContext.Notifications.Add(DomainNotification.Create(Guid.CreateVersion7(friendship), anaId, beaId, NotificationType.FriendshipStarted, null, null, null, friendship));
        dbContext.Notifications.Add(DomainNotification.Create(Guid.CreateVersion7(follow), anaId, carlosId, NotificationType.UserFollowed, null, null, null, follow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
