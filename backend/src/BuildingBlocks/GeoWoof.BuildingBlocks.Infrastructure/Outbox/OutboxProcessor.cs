using System.Text.Json;
using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoWoof.BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// Delivers the pending Outbox messages of one module to their consumers. Batches are locked with
/// <c>FOR UPDATE SKIP LOCKED</c>, so several API instances can run processors concurrently without
/// delivering the same message twice at the same time.
/// </summary>
internal sealed partial class OutboxProcessor<TContext>(
    IServiceScopeFactory scopeFactory,
    IntegrationEventDispatcher dispatcher,
    IntegrationEventTypeRegistry registry,
    OutboxSignal signal,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor<TContext>> logger) : BackgroundService
    where TContext : ModuleDbContext
{
    private static readonly TimeSpan MaxErrorBackoff = TimeSpan.FromSeconds(30);

    private readonly OutboxOptions _options = options.Value;
    private readonly OutboxRetryPolicy _retryPolicy = OutboxRetryPolicy.From(options.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schema = await ResolveSchemaAsync();
        var consecutiveErrors = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessBatchAsync(schema, stoppingToken);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A long-running processor must survive transient database or dispatch errors; it backs off and retries.
                consecutiveErrors++;
                LogBatchFailed(logger, exception, schema, consecutiveErrors);
            }

            if (processed == _options.BatchSize)
            {
                continue;
            }

            var wait = consecutiveErrors == 0
                ? _options.PollingInterval
                : TimeSpan.FromTicks(Math.Min(_options.PollingInterval.Ticks << Math.Min(consecutiveErrors, 4), MaxErrorBackoff.Ticks));
            await signal.WaitAsync(schema, wait, timeProvider, stoppingToken);
        }
    }

    /// <summary>Locks and dispatches one batch. Returns the number of messages handled (delivered or failed).</summary>
    internal async Task<int> ProcessBatchAsync(string schema, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var sql = BuildPendingMessagesQuery(schema);
        var messages = await dbContext.OutboxMessages
            .FromSqlRaw(sql, timeProvider.GetUtcNow(), _options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            await ProcessMessageAsync(message, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    private async Task ProcessMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var eventType = registry.Resolve(message.Type);
        if (eventType is null)
        {
            message.MarkPermanentlyFailed($"Unknown integration event type '{message.Type}'.", timeProvider.GetUtcNow());
            LogUnknownType(logger, message.Id, message.Type);
            return;
        }

        IIntegrationEvent integrationEvent;
        try
        {
            integrationEvent = IntegrationEventSerializer.Deserialize(message.Payload, eventType);
        }
        catch (JsonException exception)
        {
            message.MarkPermanentlyFailed(exception.Message, timeProvider.GetUtcNow());
            LogMalformedPayload(logger, exception, message.Id, message.Type);
            return;
        }

        try
        {
            await dispatcher.DispatchAsync(message.Id, integrationEvent, cancellationToken);
            message.MarkProcessed(timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            message.MarkAttemptFailed(exception.ToString(), timeProvider.GetUtcNow(), _retryPolicy);
            if (message.FailedAt is null)
            {
                LogDeliveryFailed(logger, exception, message.Id, message.Type, message.Attempts, message.NextAttemptAt);
            }
            else
            {
                LogDeliveryAbandoned(logger, exception, message.Id, message.Type, message.Attempts);
            }
        }
    }

    // The schema comes from code (the module's DbContext), never from user input.
    private static string BuildPendingMessagesQuery(string schema) =>
        "SELECT * FROM \"" + schema + "\"." + OutboxMessageConfiguration.TableName + " " +
        "WHERE processed_at IS NULL AND failed_at IS NULL " +
        "AND (next_attempt_at IS NULL OR next_attempt_at <= {0}) " +
        "ORDER BY occurred_at, id " +
        "LIMIT {1} " +
        "FOR UPDATE SKIP LOCKED";

    private async Task<string> ResolveSchemaAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<TContext>().Schema;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch for schema {Schema} failed ({ConsecutiveErrors} in a row)")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception, string schema, int consecutiveErrors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} has unknown type {EventType}; it will not be retried")]
    private static partial void LogUnknownType(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) has a malformed payload; it will not be retried")]
    private static partial void LogMalformedPayload(ILogger logger, Exception exception, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery of outbox message {MessageId} ({EventType}) failed on attempt {Attempts}; next attempt at {NextAttemptAt}")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, Guid messageId, string eventType, int attempts, DateTimeOffset? nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery of outbox message {MessageId} ({EventType}) abandoned after {Attempts} attempts")]
    private static partial void LogDeliveryAbandoned(ILogger logger, Exception exception, Guid messageId, string eventType, int attempts);
}
