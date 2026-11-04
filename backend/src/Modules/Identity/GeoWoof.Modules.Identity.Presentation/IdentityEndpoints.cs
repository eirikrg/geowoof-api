using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Identity.Application;
using GeoWoof.Modules.Identity.Application.Account;
using GeoWoof.Modules.Identity.Application.Authentication;
using GeoWoof.Modules.Identity.Application.EmailVerification;
using GeoWoof.Modules.Identity.Application.Passwords;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Identity.Presentation;

/// <summary>Minimal API endpoints of the Identity module, mapped under <c>/api/v1</c>.</summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var auth = api.MapGroup("/auth").WithTags("Authentication");
        auth.MapGet("/providers", GetProvidersAsync).AllowAnonymous()
            .Produces<AuthProvidersDto>();
        auth.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication)
            .Produces<SessionDto>(StatusCodes.Status201Created).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication)
            .Produces<SessionDto>().ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
        auth.MapPost("/google", SignInWithGoogleAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication)
            .Produces<SessionDto>().ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status404NotFound);
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.TokenRefresh)
            .Produces<SessionDto>().ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status409Conflict);
        auth.MapPost("/logout", LogoutAsync)
            .Produces(StatusCodes.Status204NoContent);
        auth.MapPost("/email/resend", ResendEmailVerificationAsync).RequireRateLimiting(RateLimitPolicies.Authentication)
            .Produces(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/email/verify", VerifyEmailAsync).RequireRateLimiting(RateLimitPolicies.Authentication)
            .Produces<AccessTokenDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        auth.MapPost("/password/forgot", ForgotPasswordAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication)
            .Produces(StatusCodes.Status202Accepted).ProducesValidationProblem();
        auth.MapPost("/password/reset", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication)
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
        auth.MapPost("/password/change", ChangePasswordAsync)
            .Produces<SessionDto>().ProducesValidationProblem();

        var me = api.MapGroup("/me").WithTags("Account");
        me.MapGet(string.Empty, GetMeAsync).Produces<UserDto>();
        me.MapPatch(string.Empty, UpdateMeAsync).Produces<UserDto>().ProducesValidationProblem();
        me.MapPut("/username", ChangeUsernameAsync)
            .Produces<UserDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        me.MapDelete(string.Empty, DeleteAccountAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem().ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapGet("/usernames/availability", CheckUsernameAsync).WithTags("Account")
            .Produces<UsernameAvailabilityDto>();

        return api;
    }

    private static async Task<IResult> GetProvidersAsync(
        IQueryHandler<GetAuthProvidersQuery, AuthProvidersDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetAuthProvidersQuery(), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        HttpRequest httpRequest,
        ICommandHandler<RegisterWithPasswordCommand, SessionDto> handler,
        CancellationToken cancellationToken)
    {
        var command = new RegisterWithPasswordCommand(request.Email, request.Password, request.DisplayName, RequestLanguage.Resolve(request.Language, httpRequest));
        return (await handler.Handle(command, cancellationToken)).ToHttpResult(session => TypedResults.Created("/api/v1/me", session));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        ICommandHandler<SignInWithPasswordCommand, SessionDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new SignInWithPasswordCommand(request.Email, request.Password), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> SignInWithGoogleAsync(
        GoogleSignInRequest request,
        HttpRequest httpRequest,
        ICommandHandler<SignInWithGoogleCommand, SessionDto> handler,
        CancellationToken cancellationToken)
    {
        var command = new SignInWithGoogleCommand(request.IdToken, RequestLanguage.Resolve(request.Language, httpRequest));
        return (await handler.Handle(command, cancellationToken)).ToHttpResult(TypedResults.Ok);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        ICommandHandler<RefreshSessionCommand, SessionDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RefreshSessionCommand(request.RefreshToken), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> LogoutAsync(
        [FromBody] LogoutRequest? request,
        ClaimsPrincipal user,
        ICommandHandler<SignOutCommand> handler,
        CancellationToken cancellationToken)
    {
        var command = new SignOutCommand(user.GetUserId(), request?.RefreshToken, request?.AllDevices ?? false);
        return (await handler.Handle(command, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> ResendEmailVerificationAsync(
        ClaimsPrincipal user,
        ICommandHandler<ResendEmailVerificationCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ResendEmailVerificationCommand(user.GetUserId()), cancellationToken);
        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.Error.ToProblem();
    }

    private static async Task<IResult> VerifyEmailAsync(
        VerifyEmailRequest request,
        ClaimsPrincipal user,
        ICommandHandler<VerifyEmailCommand, AccessTokenDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new VerifyEmailCommand(user.GetUserId(), request.Code), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        ICommandHandler<RequestPasswordResetCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RequestPasswordResetCommand(request.Email), cancellationToken);
        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.Error.ToProblem();
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        ICommandHandler<ResetPasswordCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ResetPasswordCommand(request.Email, request.Code, request.NewPassword), cancellationToken)).ToHttpResult();

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal user,
        ICommandHandler<ChangePasswordCommand, SessionDto> handler,
        CancellationToken cancellationToken)
    {
        var command = new ChangePasswordCommand(user.GetUserId(), request.CurrentPassword, request.NewPassword);
        return (await handler.Handle(command, cancellationToken)).ToHttpResult(TypedResults.Ok);
    }

    private static async Task<IResult> GetMeAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetCurrentUserQuery, UserDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetCurrentUserQuery(user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> UpdateMeAsync(
        UpdateMeRequest request,
        ClaimsPrincipal user,
        ICommandHandler<UpdateProfileCommand, UserDto> handler,
        CancellationToken cancellationToken)
    {
        var command = new UpdateProfileCommand(user.GetUserId(), request.DisplayName, request.Language);
        return (await handler.Handle(command, cancellationToken)).ToHttpResult(TypedResults.Ok);
    }

    private static async Task<IResult> ChangeUsernameAsync(
        ChangeUsernameRequest request,
        ClaimsPrincipal user,
        ICommandHandler<ChangeUsernameCommand, UserDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ChangeUsernameCommand(user.GetUserId(), request.Username), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> DeleteAccountAsync(
        [FromBody] DeleteAccountRequest request,
        ClaimsPrincipal user,
        ICommandHandler<DeleteAccountCommand> handler,
        CancellationToken cancellationToken)
    {
        var command = new DeleteAccountCommand(user.GetUserId(), request.Password, request.GoogleIdToken);
        return (await handler.Handle(command, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> CheckUsernameAsync(
        [FromQuery] string? username,
        IQueryHandler<CheckUsernameAvailabilityQuery, UsernameAvailabilityDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new CheckUsernameAvailabilityQuery(username ?? string.Empty), cancellationToken)).ToHttpResult(TypedResults.Ok);
}

/// <summary>Language for emails: the explicit request value, else the first supported <c>Accept-Language</c>.</summary>
internal static class RequestLanguage
{
    public static string? Resolve(string? requested, HttpRequest httpRequest)
    {
        if (requested is not null)
        {
            return requested;
        }

        return httpRequest.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(header => header.Quality ?? 1)
            .Select(header => header.Value.Value?.Split('-')[0])
            .FirstOrDefault(LanguageCodes.IsSupported);
    }
}
