using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Outbox;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.BuildingBlocks.UnitTests.Infrastructure;

public sealed class OutboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly OutboxRetryPolicy _policy = new(MaxAttempts: 4, BaseDelay: TimeSpan.FromSeconds(2), MaxDelay: TimeSpan.FromSeconds(10));
    private readonly IntegrationEventTypeRegistry _registry = new([typeof(PetAdopted)]);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 10)]
    [InlineData(40, 10)]
    public void Retry_delay_doubles_and_is_capped(int failedAttempts, int expectedSeconds)
    {
        _policy.DelayAfter(failedAttempts).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void New_message_stores_the_stable_type_name_and_json_payload()
    {
        var integrationEvent = new PetAdopted(Guid.CreateVersion7(), PetSize.Large, Now);

        var message = OutboxMessage.Create(integrationEvent, _registry);

        message.Id.ShouldBe(integrationEvent.Id);
        message.Type.ShouldBe("tests.pet_adopted.v1");
        message.OccurredAt.ShouldBe(Now);
        message.Payload.ShouldContain("\"size\":\"Large\"");
        message.ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public void Failed_attempts_schedule_retries_until_the_limit()
    {
        var message = OutboxMessage.Create(new PetAdopted(Guid.CreateVersion7(), PetSize.Small, Now), _registry);

        message.MarkAttemptFailed("boom", Now, _policy);
        message.Attempts.ShouldBe(1);
        message.NextAttemptAt.ShouldBe(Now.AddSeconds(2));
        message.FailedAt.ShouldBeNull();

        message.MarkAttemptFailed("boom", Now, _policy);
        message.MarkAttemptFailed("boom", Now, _policy);
        message.MarkAttemptFailed("boom", Now, _policy);

        message.Attempts.ShouldBe(4);
        message.FailedAt.ShouldBe(Now);
        message.NextAttemptAt.ShouldBeNull();
        message.LastError.ShouldBe("boom");
    }

    [Fact]
    public void Long_errors_are_truncated()
    {
        var message = OutboxMessage.Create(new PetAdopted(Guid.CreateVersion7(), PetSize.Small, Now), _registry);

        message.MarkPermanentlyFailed(new string('x', 10_000), Now);

        message.LastError!.Length.ShouldBe(4000);
        message.FailedAt.ShouldBe(Now);
    }

    [Fact]
    public void Processing_clears_the_retry_state()
    {
        var message = OutboxMessage.Create(new PetAdopted(Guid.CreateVersion7(), PetSize.Small, Now), _registry);
        message.MarkAttemptFailed("boom", Now, _policy);

        message.MarkProcessed(Now.AddMinutes(1));

        message.ProcessedAt.ShouldBe(Now.AddMinutes(1));
        message.NextAttemptAt.ShouldBeNull();
        message.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Signal_wakes_up_a_waiting_processor()
    {
        var signal = new OutboxSignal();
        var waiting = signal.WaitAsync("pets", TimeSpan.FromHours(1), TimeProvider.System, TestContext.Current.CancellationToken);

        signal.Notify("pets");

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        waiting.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Signal_wait_ends_when_the_polling_interval_elapses()
    {
        var time = new FakeTimeProvider(Now);
        var signal = new OutboxSignal();
        var waiting = signal.WaitAsync("pets", TimeSpan.FromSeconds(2), time, TestContext.Current.CancellationToken);

        waiting.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(2));

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        waiting.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Signals_for_other_schemas_do_not_wake_a_processor()
    {
        var time = new FakeTimeProvider(Now);
        var signal = new OutboxSignal();
        var waiting = signal.WaitAsync("pets", TimeSpan.FromSeconds(2), time, TestContext.Current.CancellationToken);

        signal.Notify("walks");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        waiting.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(2));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}

public enum PetSize
{
    Small,
    Large,
}

[EventType("tests.pet_adopted.v1")]
public sealed record PetAdopted(Guid PetId, PetSize Size, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
