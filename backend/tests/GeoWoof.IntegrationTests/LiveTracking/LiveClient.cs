using System.Text.Json;
using System.Threading.Channels;
using GeoWoof.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GeoWoof.IntegrationTests.LiveTracking;

/// <summary>A real client of the hub. It keeps what arrives as JSON, so the tests also check what travels.</summary>
internal sealed class LiveClient : IAsyncDisposable
{
    /// <summary>How long a test waits to be sure that something does not arrive.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(700);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly Channel<(string Name, JsonElement Payload)> _events = Channel.CreateUnbounded<(string, JsonElement)>();

    private LiveClient(HubConnection connection)
    {
        Connection = connection;
        connection.On<JsonElement>("WalkerUpdated", payload => _events.Writer.TryWrite(("WalkerUpdated", payload)));
        connection.On<JsonElement>("WalkerLeft", payload => _events.Writer.TryWrite(("WalkerLeft", payload)));
    }

    public HubConnection Connection { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static LiveClient Create(ApiFactory api, TestUser user)
    {
        var accessToken = user.Client.DefaultRequestHeaders.Authorization!.Parameter;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/live"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(accessToken);
            })
            .Build();
        return new LiveClient(connection);
    }

    public static async Task<LiveClient> ConnectAsync(ApiFactory api, TestUser user)
    {
        var client = Create(api, user);
        await client.Connection.StartAsync(Token);
        return client;
    }

    public async Task<JsonElement> LookAtAsync(double latitude, double longitude, double margin = 0.001) =>
        await Connection.InvokeAsync<JsonElement>(
            "SubscribeViewport",
            new { north = latitude + margin, south = latitude - margin, east = longitude + margin, west = longitude - margin },
            Token);

    public Task<JsonElement> StartAsync(Guid petId) =>
        Connection.InvokeAsync<JsonElement>("StartSharing", new { walkId = Guid.CreateVersion7(), petIds = new[] { petId } }, Token);

    public Task SendPositionAsync(double latitude, double longitude) =>
        Connection.InvokeAsync(
            "UpdatePosition",
            new { latitude, longitude, accuracyM = 8.5, recordedAt = DateTimeOffset.UtcNow },
            Token);

    public Task StopAsync() => Connection.InvokeAsync("StopSharing", Token);

    /// <returns>The next event, which has to be the one expected.</returns>
    public async Task<JsonElement> NextAsync(string name)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Patience);
        var (received, payload) = await _events.Reader.ReadAsync(timeout.Token);
        received.ShouldBe(name);
        return payload;
    }

    public async Task ShouldReceiveNothingAsync()
    {
        await Task.Delay(Silence, Token);
        if (_events.Reader.TryRead(out var received))
        {
            throw new ShouldAssertException($"Nothing should have arrived, but {received.Name} did: {received.Payload}");
        }
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

internal static class LiveClientExtensions
{
    /// <summary>The code of a failed call, which is all a client gets of it.</summary>
    public static async Task<string> FailureCodeAsync(this Task call)
    {
        var failure = await Should.ThrowAsync<HubException>(call);
        return failure.Message.Split(": ")[^1];
    }
}
