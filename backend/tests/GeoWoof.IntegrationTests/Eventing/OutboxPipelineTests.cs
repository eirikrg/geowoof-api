using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.IntegrationTests.Infrastructure;

namespace GeoWoof.IntegrationTests.Eventing;

/// <summary>
/// End-to-end tests of command → domain event → integration event → Outbox → processor → consumers
/// (with Inbox idempotency), against a real PostgreSQL database per test.
/// </summary>
public sealed class OutboxPipelineTests(TestInfrastructure infrastructure)
{
    private static readonly string[] AllConsumers = [FlakyConsumer.Name, PickyConsumer.Name, ReliableConsumer.Name];

    [Fact]
    public async Task Integration_event_is_delivered_once_to_every_consumer()
    {
        await using var host = await EventingTestHost.StartAsync(await infrastructure.CreateDatabaseAsync(TestContext.Current.CancellationToken));

        var kennelId = (await host.OpenKennelAsync("Happy Paws")).Value;

        var audit = await Eventually.Until(host.AuditAsync, entries => entries.Count == AllConsumers.Length, "all consumers recorded the kennel");
        audit.ShouldAllBe(entry => entry.KennelId == kennelId);
        audit.Select(entry => entry.Consumer).Order(StringComparer.Ordinal).ShouldBe(AllConsumers);

        var message = (await Eventually.Until(host.OutboxAsync, messages => messages.Single().ProcessedAt is not null, "the message is processed")).Single();
        message.Type.ShouldBe("tests.kennel_opened.v1");
        message.Attempts.ShouldBe(0);
        message.Payload.ShouldContain(kennelId.ToString());
        (await host.InboxCountAsync()).ShouldBe(AllConsumers.Length);
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_without_repeating_successful_consumers()
    {
        await using var host = await EventingTestHost.StartAsync(await infrastructure.CreateDatabaseAsync(TestContext.Current.CancellationToken));
        host.Behavior.FailNextDeliveries(2);

        await host.OpenKennelAsync("Second Chance");

        var message = (await Eventually.Until(host.OutboxAsync, messages => messages.Single().ProcessedAt is not null, "the message is processed after retries")).Single();
        message.Attempts.ShouldBe(2);
        message.LastError.ShouldBeNull();
        var audit = await host.AuditAsync();
        audit.Count.ShouldBe(AllConsumers.Length);
        audit.Count(entry => entry.Consumer == ReliableConsumer.Name).ShouldBe(1);
    }

    [Fact]
    public async Task Message_that_keeps_failing_is_abandoned_after_the_maximum_attempts()
    {
        await using var host = await EventingTestHost.StartAsync(await infrastructure.CreateDatabaseAsync(TestContext.Current.CancellationToken));

        await host.OpenKennelAsync("poison pill");

        var message = (await Eventually.Until(host.OutboxAsync, messages => messages.Single().FailedAt is not null, "the message is abandoned")).Single();
        message.Attempts.ShouldBe(EventingTestHost.MaxAttempts);
        message.ProcessedAt.ShouldBeNull();
        message.LastError.ShouldNotBeNull().ShouldContain("can never be processed");
        var audit = await host.AuditAsync();
        audit.Select(entry => entry.Consumer).Order(StringComparer.Ordinal).ShouldBe([FlakyConsumer.Name, ReliableConsumer.Name]);
    }

    [Fact]
    public async Task Nothing_is_published_when_the_business_transaction_fails()
    {
        await using var host = await EventingTestHost.StartAsync(await infrastructure.CreateDatabaseAsync(TestContext.Current.CancellationToken));
        await host.OpenKennelAsync("Duplicate");

        await Should.ThrowAsync<UniqueConstraintViolationException>(() => host.OpenKennelAsync("Duplicate"));

        (await host.OutboxAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_commands_never_reach_the_database()
    {
        await using var host = await EventingTestHost.StartAsync(await infrastructure.CreateDatabaseAsync(TestContext.Current.CancellationToken));

        var result = await host.OpenKennelAsync(string.Empty);

        result.IsFailure.ShouldBeTrue();
        result.Error.FieldErrors.ShouldNotBeNull().Keys.ShouldBe(["name"]);
        (await host.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Competing_processors_never_deliver_a_message_twice()
    {
        const int kennels = 25;
        var database = await infrastructure.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await using var first = await EventingTestHost.StartAsync(database);
        await using var second = await EventingTestHost.StartAsync(database, createSchema: false);

        for (var i = 0; i < kennels; i++)
        {
            await (i % 2 == 0 ? first : second).OpenKennelAsync($"Kennel {i}");
        }

        var outbox = await Eventually.Until(first.OutboxAsync, messages => messages.Count == kennels && messages.All(m => m.ProcessedAt is not null), "every message is processed");
        outbox.ShouldAllBe(message => message.Attempts == 0);
        var audit = await first.AuditAsync();
        audit.Count.ShouldBe(kennels * AllConsumers.Length);
        audit.GroupBy(entry => (entry.KennelId, entry.Consumer)).ShouldAllBe(group => group.Count() == 1);
    }
}
