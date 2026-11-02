using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Infrastructure.Inbox;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// Delivers an integration event to every subscribed consumer. Each consumer runs in its own DI scope
/// and database transaction, and its Inbox record is written in that same transaction, so a
/// redelivered message is skipped by consumers that already processed it.
/// </summary>
internal sealed class IntegrationEventDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILookup<Type, IntegrationEventConsumerRegistration> _consumersByEventType;

    public IntegrationEventDispatcher(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IntegrationEventTypeRegistry registry,
        IEnumerable<IntegrationEventConsumerRegistration> consumers)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        var registrations = consumers.ToList();

        var unregistered = registrations.Where(consumer => !registry.IsRegistered(consumer.EventType)).ToList();
        if (unregistered.Count > 0)
        {
            var details = string.Join(", ", unregistered.Select(c => $"{c.ConsumerName} → {c.EventType.FullName}"));
            throw new InvalidOperationException(
                $"Consumers subscribe to integration events whose Contracts assembly is not registered: {details}.");
        }

        _consumersByEventType = registrations.ToLookup(consumer => consumer.EventType);
    }

    /// <summary>
    /// Delivers to every consumer, even if some fail, so that one failing consumer never blocks the
    /// others. Throws if any consumer failed; on redelivery only the failed ones run again.
    /// </summary>
    public async Task DispatchAsync(Guid messageId, IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        List<Exception>? failures = null;
        foreach (var consumer in _consumersByEventType[integrationEvent.GetType()])
        {
            try
            {
                await DispatchToConsumerAsync(messageId, integrationEvent, consumer, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures ??= [];
                failures.Add(new InvalidOperationException($"Consumer {consumer.ConsumerName} failed: {exception.Message}", exception));
            }
        }

        if (failures is not null)
        {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    private async Task DispatchToConsumerAsync(
        Guid messageId,
        IIntegrationEvent integrationEvent,
        IntegrationEventConsumerRegistration consumer,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = (ModuleDbContext)services.GetRequiredService(consumer.DbContextType);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var alreadyProcessed = await dbContext.InboxMessages.AnyAsync(
            message => message.MessageId == messageId && message.Consumer == consumer.ConsumerName,
            cancellationToken);
        if (alreadyProcessed)
        {
            return;
        }

        var handler = services.GetRequiredService(consumer.HandlerType);
        await consumer.InvokeAsync(handler, integrationEvent, cancellationToken);

        dbContext.InboxMessages.Add(new InboxMessage(messageId, consumer.ConsumerName, _timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
