namespace GeoWoof.IntegrationTests.Infrastructure;

/// <summary>Polls an asynchronous probe until a condition holds (for background processing).</summary>
internal static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<T> Until<T>(Func<Task<T>> probe, Func<T, bool> condition, string description, TimeSpan? timeout = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var value = await probe();
            if (condition(value))
            {
                return value;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {description}. Last value: {value}");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
