using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Chat.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Chat.Presentation;

public sealed record SendMessageRequest(Guid ClientMessageId, string? Body, Guid? ImageAssetId);

public sealed record ReadRequest(Guid LastReadMessageId);

public sealed record TypingRequest(Guid ConversationId);

/// <summary>Minimal API endpoints of the Chat module, mapped under <c>/api/v1</c>.</summary>
public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var conversations = api.MapGroup("/conversations").WithTags("Chat");
        conversations.MapGet(string.Empty, GetConversationsAsync).Produces<PagedResponse<ConversationDto>>().ProducesProblem(StatusCodes.Status400BadRequest);
        conversations.MapGet("/unread-count", GetUnreadAsync).Produces<CountDto>();
        conversations.MapPut("/direct/{userId:guid}", OpenDirectAsync)
            .Produces<ConversationDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        conversations.MapGet("/{id:guid}", GetConversationAsync).Produces<ConversationDto>().ProducesProblem(StatusCodes.Status404NotFound);
        conversations.MapGet("/{id:guid}/messages", GetMessagesAsync)
            .Produces<PagedResponse<MessageDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        conversations.MapPost("/{id:guid}/messages", SendMessageAsync)
            .RequireRateLimiting(RateLimitPolicies.ChatMessages)
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .Produces<MessageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        conversations.MapPost("/{id:guid}/read", MarkReadAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> GetConversationsAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetConversationsQuery, PagedResponse<ConversationDto>> handler,
        CancellationToken cancellationToken,
        [FromQuery] string? cursor = null,
        [FromQuery] int? limit = null) =>
        (await handler.Handle(new GetConversationsQuery(user.GetUserId(), cursor, limit), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetUnreadAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetUnreadMessagesQuery, CountDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetUnreadMessagesQuery(user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> OpenDirectAsync(
        Guid userId,
        ClaimsPrincipal user,
        ICommandHandler<OpenDirectConversationCommand, ConversationDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new OpenDirectConversationCommand(user.GetUserId(), userId), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetConversationAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetConversationQuery, ConversationDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetConversationQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetMessagesAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetMessagesQuery, PagedResponse<MessageDto>> handler,
        CancellationToken cancellationToken,
        [FromQuery] string? cursor = null,
        [FromQuery] Guid? after = null,
        [FromQuery] int? limit = null) =>
        (await handler.Handle(new GetMessagesQuery(user.GetUserId(), id, cursor, after, limit), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> SendMessageAsync(
        Guid id,
        SendMessageRequest request,
        ClaimsPrincipal user,
        ICommandHandler<SendMessageCommand, SentMessageDto> handler,
        CancellationToken cancellationToken)
    {
        var command = new SendMessageCommand(user.GetUserId(), id, request.ClientMessageId, request.Body, request.ImageAssetId);
        return (await handler.Handle(command, cancellationToken)).ToHttpResult(sent => sent.IsNew
            ? TypedResults.Created($"/api/v1/conversations/{id}/messages/{sent.Message.Id}", sent.Message)
            : TypedResults.Ok(sent.Message));
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id,
        ReadRequest request,
        ClaimsPrincipal user,
        ICommandHandler<MarkConversationReadCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new MarkConversationReadCommand(user.GetUserId(), id, request.LastReadMessageId), cancellationToken)).ToHttpResult();
}

/// <summary>Messages in real time. It only tells: sending and reading go by REST.</summary>
[Authorize]
public sealed class ChatHub(ICommandHandler<SetTypingCommand> setTyping, ChatConnections connections, TimeProvider timeProvider) : Hub
{
    public const string MessageReceived = nameof(MessageReceived);
    public const string ConversationRead = nameof(ConversationRead);
    public const string TypingChanged = nameof(TypingChanged);

    private const string LastTypingKey = "typing";
    private static readonly TimeSpan MinTypingInterval = TimeSpan.FromSeconds(3);

    private Guid UserId => Context.User!.GetUserId();

    public override async Task OnConnectedAsync()
    {
        if (!await connections.OpenAsync(UserId, Context.ConnectionId, Context.ConnectionAborted))
        {
            await connections.CloseAsync(UserId, Context.ConnectionId, CancellationToken.None);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await connections.CloseAsync(UserId, Context.ConnectionId, CancellationToken.None);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SetTyping(TypingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The items of a connection are not shared: a connection handles its calls one at a time.
        var now = timeProvider.GetUtcNow();
        if (Context.Items.TryGetValue(LastTypingKey, out var value) && value is DateTimeOffset last && now - last < MinTypingInterval)
        {
            return;
        }

        Context.Items[LastTypingKey] = now;
        Check(await setTyping.Handle(new SetTypingCommand(UserId, request.ConversationId), Context.ConnectionAborted));
    }

    private static void Check(Result result)
    {
        if (result.IsFailure)
        {
            throw new HubException(result.Error.Code);
        }
    }
}

/// <summary>Reaches the people connected, also in other instances, with the backplane.</summary>
internal sealed class HubChatNotifier(IHubContext<ChatHub> hub) : IChatNotifier
{
    public Task MessageReceivedAsync(IReadOnlyList<Guid> userIds, MessageDto message, CancellationToken cancellationToken) =>
        hub.Clients.Users(Names(userIds)).SendAsync(ChatHub.MessageReceived, message, cancellationToken);

    public Task ConversationReadAsync(IReadOnlyList<Guid> userIds, ConversationReadDto read, CancellationToken cancellationToken) =>
        hub.Clients.Users(Names(userIds)).SendAsync(ChatHub.ConversationRead, read, cancellationToken);

    public Task TypingChangedAsync(Guid userId, TypingDto typing, CancellationToken cancellationToken) =>
        hub.Clients.User(userId.ToString()).SendAsync(ChatHub.TypingChanged, typing, cancellationToken);

    private static string[] Names(IReadOnlyList<Guid> userIds) => userIds.Select(userId => userId.ToString()).ToArray();
}

public static class ChatPresentation
{
    public const string HubPath = "/hubs/chat";

    public static ISignalRServerBuilder AddChatHub(this ISignalRServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IChatNotifier, HubChatNotifier>();
        return builder;
    }

    public static IEndpointRouteBuilder MapChatHub(this IEndpointRouteBuilder app)
    {
        // When the token expires the connection closes, and the app comes back with a new one.
        app.MapHub<ChatHub>(HubPath, (HttpConnectionDispatcherOptions options) => options.CloseOnAuthenticationExpiration = true);
        return app;
    }
}
