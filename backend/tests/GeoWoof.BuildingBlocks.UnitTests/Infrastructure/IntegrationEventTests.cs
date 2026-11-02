using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeoWoof.BuildingBlocks.UnitTests.Infrastructure;

public sealed class IntegrationEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registry_maps_types_and_names_both_ways()
    {
        var registry = new IntegrationEventTypeRegistry([typeof(PetAdopted)]);

        registry.GetName(typeof(PetAdopted)).ShouldBe("tests.pet_adopted.v1");
        registry.Resolve("tests.pet_adopted.v1").ShouldBe(typeof(PetAdopted));
        registry.Resolve("tests.unknown.v1").ShouldBeNull();
        registry.IsRegistered(typeof(PetAdopted)).ShouldBeTrue();
    }

    [Fact]
    public void Registry_rejects_events_without_a_stable_name()
    {
        var exception = Should.Throw<InvalidOperationException>(() => new IntegrationEventTypeRegistry([typeof(UnnamedEvent)]));

        exception.Message.ShouldContain("[EventType");
    }

    [Fact]
    public void Registry_rejects_duplicated_names()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => new IntegrationEventTypeRegistry([typeof(PetAdopted), typeof(DuplicatedPetAdopted)]));

        exception.Message.ShouldContain("tests.pet_adopted.v1");
    }

    [Fact]
    public void Unregistered_events_cannot_be_published()
    {
        var registry = new IntegrationEventTypeRegistry([typeof(PetAdopted)]);
        using var buffer = new IntegrationEventBuffer(registry, NullLogger<IntegrationEventBuffer>.Instance);

        Should.Throw<InvalidOperationException>(() => buffer.Publish(new UnnamedEvent(Now)));
        buffer.Count.ShouldBe(0);
    }

    [Fact]
    public void Draining_the_buffer_returns_events_in_publication_order_and_empties_it()
    {
        var registry = new IntegrationEventTypeRegistry([typeof(PetAdopted)]);
        using var buffer = new IntegrationEventBuffer(registry, NullLogger<IntegrationEventBuffer>.Instance);
        var first = new PetAdopted(Guid.CreateVersion7(), PetSize.Small, Now);
        var second = new PetAdopted(Guid.CreateVersion7(), PetSize.Large, Now);

        buffer.Publish(first);
        buffer.Publish(second);

        buffer.Drain().ShouldBe([first, second]);
        buffer.Drain().ShouldBeEmpty();
    }

    [Fact]
    public void Serialization_round_trip_keeps_the_event_identity()
    {
        var original = new PetAdopted(Guid.CreateVersion7(), PetSize.Large, Now);

        var payload = IntegrationEventSerializer.Serialize(original);
        var restored = IntegrationEventSerializer.Deserialize(payload, typeof(PetAdopted));

        restored.ShouldBe(original);
        restored.Id.ShouldBe(original.Id);
    }

    [Fact]
    public void Dispatcher_refuses_consumers_of_unregistered_events()
    {
        var registry = new IntegrationEventTypeRegistry([typeof(PetAdopted)]);
        var consumer = new IntegrationEventConsumerRegistration(typeof(UnnamedEvent), typeof(UnnamedEventConsumer), typeof(SampleDbContext));
        using var services = new ServiceCollection().BuildServiceProvider();

        var exception = Should.Throw<InvalidOperationException>(() => new IntegrationEventDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            registry,
            [consumer]));

        exception.Message.ShouldContain(nameof(UnnamedEventConsumer));
    }

    [Fact]
    public void Consumers_are_discovered_with_their_inbox_context()
    {
        var services = new ServiceCollection();

        services.AddIntegrationEventConsumers<SampleDbContext>(typeof(IntegrationEventTests).Assembly);

        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IntegrationEventConsumerRegistration))
            .Select(descriptor => (IntegrationEventConsumerRegistration)descriptor.ImplementationInstance!)
            .ToList();
        var registration = registrations.ShouldHaveSingleItem();
        registration.EventType.ShouldBe(typeof(UnnamedEvent));
        registration.HandlerType.ShouldBe(typeof(UnnamedEventConsumer));
        registration.DbContextType.ShouldBe(typeof(SampleDbContext));
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(UnnamedEventConsumer));
    }
}

public sealed record UnnamedEvent(DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("tests.pet_adopted.v1")]
public sealed record DuplicatedPetAdopted(DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

internal sealed class UnnamedEventConsumer : IIntegrationEventHandler<UnnamedEvent>
{
    public Task Handle(UnnamedEvent integrationEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : ModuleDbContext(options)
{
    public override string Schema => "samples";

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
    }
}
