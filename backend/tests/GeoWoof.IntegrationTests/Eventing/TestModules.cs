using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Two minimal modules that exist only in this test project, to exercise the real eventing pipeline:
// "kennels" produces an integration event from a domain event; "audit" consumes it.
namespace GeoWoof.IntegrationTests.Eventing;

// ── Producer module: kennels ────────────────────────────────────────────────

public sealed class Kennel : AggregateRoot
{
    private Kennel()
    {
        Name = string.Empty;
    }

    private Kennel(Guid id, string name)
        : base(id)
    {
        Name = name;
    }

    public string Name { get; private set; }

    public static Kennel Open(string name)
    {
        var kennel = new Kennel(Guid.CreateVersion7(), name);
        kennel.Raise(new KennelOpened(kennel.Id, name));
        return kennel;
    }
}

public sealed record KennelOpened(Guid KennelId, string Name) : IDomainEvent;

[EventType("tests.kennel_opened.v1")]
public sealed record KennelOpenedIntegrationEvent(Guid KennelId, string Name, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

public sealed record OpenKennelCommand(string Name) : ICommand<Guid>;

internal sealed class OpenKennelCommandValidator : AbstractValidator<OpenKennelCommand>
{
    public OpenKennelCommandValidator() => RuleFor(command => command.Name).NotEmpty().MaximumLength(50);
}

internal sealed class OpenKennelCommandHandler(KennelsDbContext dbContext) : ICommandHandler<OpenKennelCommand, Guid>
{
    public async Task<Result<Guid>> Handle(OpenKennelCommand command, CancellationToken cancellationToken)
    {
        var kennel = Kennel.Open(command.Name);
        dbContext.Kennels.Add(kennel);
        await dbContext.SaveChangesAsync(cancellationToken);
        return kennel.Id;
    }
}

/// <summary>Translates the domain event into the public integration event (same transaction).</summary>
internal sealed class PublishKennelOpened(IIntegrationEventPublisher publisher, TimeProvider timeProvider)
    : IDomainEventHandler<KennelOpened>
{
    public Task Handle(KennelOpened domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new KennelOpenedIntegrationEvent(domainEvent.KennelId, domainEvent.Name, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

public sealed class KennelsDbContext(DbContextOptions<KennelsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "kennels";

    public override string Schema => SchemaName;

    public DbSet<Kennel> Kennels => Set<Kennel>();

    protected override void ConfigureModule(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Kennel>(kennel =>
        {
            kennel.HasKey(k => k.Id);
            kennel.Property(k => k.Name).HasMaxLength(50);
            kennel.HasIndex(k => k.Name).IsUnique();
        });
}

// ── Consumer module: audit ──────────────────────────────────────────────────

public sealed class AuditEntry
{
    public Guid Id { get; set; }

    public Guid KennelId { get; set; }

    public string Consumer { get; set; } = string.Empty;
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "audit";

    public override string Schema => SchemaName;

    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    // A duplicated delivery to the same consumer would violate this index.
    protected override void ConfigureModule(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.HasKey(e => e.Id);
            entry.HasIndex(e => new { e.KennelId, e.Consumer }).IsUnique();
        });
}

/// <summary>Lets a test make the flaky consumer fail a given number of times.</summary>
public sealed class ConsumerBehavior
{
    private int _failuresRemaining;

    public void FailNextDeliveries(int count) => Interlocked.Exchange(ref _failuresRemaining, count);

    public bool ShouldFail() => Interlocked.Decrement(ref _failuresRemaining) >= 0;
}

internal abstract class AuditConsumer(AuditDbContext dbContext, string consumer)
{
    protected async Task RecordAsync(KennelOpenedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        dbContext.Entries.Add(new AuditEntry { Id = Guid.CreateVersion7(), KennelId = integrationEvent.KennelId, Consumer = consumer });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class ReliableConsumer(AuditDbContext dbContext)
    : AuditConsumer(dbContext, Name), IIntegrationEventHandler<KennelOpenedIntegrationEvent>
{
    public const string Name = "reliable";

    public Task Handle(KennelOpenedIntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        RecordAsync(integrationEvent, cancellationToken);
}

internal sealed class FlakyConsumer(AuditDbContext dbContext, ConsumerBehavior behavior)
    : AuditConsumer(dbContext, Name), IIntegrationEventHandler<KennelOpenedIntegrationEvent>
{
    public const string Name = "flaky";

    public Task Handle(KennelOpenedIntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        behavior.ShouldFail()
            ? throw new InvalidOperationException("Simulated transient failure.")
            : RecordAsync(integrationEvent, cancellationToken);
}

/// <summary>Always fails for kennels whose name starts with "poison" (a message that can never succeed).</summary>
internal sealed class PickyConsumer(AuditDbContext dbContext)
    : AuditConsumer(dbContext, Name), IIntegrationEventHandler<KennelOpenedIntegrationEvent>
{
    public const string Name = "picky";

    public Task Handle(KennelOpenedIntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        integrationEvent.Name.StartsWith("poison", StringComparison.Ordinal)
            ? throw new InvalidOperationException("This message can never be processed.")
            : RecordAsync(integrationEvent, cancellationToken);
}
