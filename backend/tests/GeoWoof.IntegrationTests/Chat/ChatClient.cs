using System.Text.Json;
using System.Threading.Channels;
using GeoWoof.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace GeoWoof.IntegrationTests.Chat;

/// <summary>A real client of the hub. It keeps what arrives as JSON, so the tests also check what travels.</summary>
internal sealed class ChatClient : IAsyncDisposable
{
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly Channel<(string Name, JsonElement Payload)> _events = Channel.CreateUnbounded<(string, JsonElement)>();

    private ChatClient(HubConnection connection)
    {
        Connection = connection;
        foreach (var name in new[] { "MessageReceived", "ConversationRead", "TypingChanged" })
        {
            connection.On<JsonElement>(name, payload => _events.Writer.TryWrite((name, payload)));
        }
    }

    public HubConnection Connection { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static ChatClient Create(ApiFactory api, TestUser user)
    {
        var accessToken = user.Client.DefaultRequestHeaders.Authorization!.Parameter;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(accessToken);
            })
            .Build();
        return new ChatClient(connection);
    }

    public static async Task<ChatClient> ConnectAsync(ApiFactory api, TestUser user)
    {
        var client = Create(api, user);
        await client.Connection.StartAsync(Token);
        return client;
    }

    public Task SetTypingAsync(Guid conversationId) =>
        Connection.InvokeAsync("SetTyping", new { conversationId }, Token);

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
