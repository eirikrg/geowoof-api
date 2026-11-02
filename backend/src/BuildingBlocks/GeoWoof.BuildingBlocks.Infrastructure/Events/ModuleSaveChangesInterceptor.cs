using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Infrastructure.Outbox;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// Runs on every <c>SaveChangesAsync</c> of a <see cref="ModuleDbContext"/>, before the commit:
/// dispatches the domain events raised by tracked aggregates (repeatedly, as handlers may raise
/// more), then moves the published integration events into the module's Outbox so they are saved
/// atomically with the business change. After a successful save it wakes up the outbox processor.
/// </summary>
internal sealed class ModuleSaveChangesInterceptor(
    DomainEventDispatcher domainEventDispatcher,
    IntegrationEventBuffer integrationEvents,
    IntegrationEventTypeRegistry registry,
    OutboxSignal outboxSignal) : SaveChangesInterceptor
{
    private const int MaxDomainEventRounds = 10;

    private readonly HashSet<DbContext> _contextsWithNewOutboxMessages = new(ReferenceEqualityComparer.Instance);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        eventData.Context is ModuleDbContext
            ? throw new InvalidOperationException("Module DbContexts must be saved with SaveChangesAsync so domain events can be dispatched.")
            : result;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ModuleDbContext context)
        {
            await DispatchDomainEventsAsync(context, cancellationToken);
            if (MoveIntegrationEventsToOutbox(context))
            {
                _contextsWithNewOutboxMessages.Add(context);
            }
        }

        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ModuleDbContext context && _contextsWithNewOutboxMessages.Remove(context))
        {
            outboxSignal.Notify(context.Schema);
        }

        return ValueTask.FromResult(result);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            _contextsWithNewOutboxMessages.Remove(eventData.Context);
        }

        return Task.CompletedTask;
    }

    private async Task DispatchDomainEventsAsync(ModuleDbContext context, CancellationToken cancellationToken)
    {
        for (var round = 1; ; round++)
        {
            var aggregates = context.ChangeTracker.Entries<AggregateRoot>()
                .Select(entry => entry.Entity)
                .Where(aggregate => aggregate.DomainEvents.Count > 0)
                .ToList();
            if (aggregates.Count == 0)
            {
                return;
            }

            if (round > MaxDomainEventRounds)
            {
                throw new InvalidOperationException(
                    $"Domain event handlers kept raising events after {MaxDomainEventRounds} rounds; check for cycles.");
            }

            var domainEvents = aggregates.SelectMany(aggregate => aggregate.DomainEvents).ToList();
            aggregates.ForEach(aggregate => aggregate.ClearDomainEvents());
            foreach (var domainEvent in domainEvents)
            {
                await domainEventDispatcher.DispatchAsync(domainEvent, cancellationToken);
            }
        }
    }

    private bool MoveIntegrationEventsToOutbox(ModuleDbContext context)
    {
        var pending = integrationEvents.Drain();
        foreach (var integrationEvent in pending)
        {
            context.OutboxMessages.Add(OutboxMessage.Create(integrationEvent, registry));
        }

        return pending.Count > 0;
    }
}
