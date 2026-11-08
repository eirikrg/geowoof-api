using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Identity.Domain;

/// <summary>Business errors of the Identity module. Codes are part of the API contract.</summary>
public static class IdentityErrors
{
    public static readonly Error InvalidCursor = Error.Validation("request.invalid", "The cursor is not valid.");

    public static readonly Error UserNotFound =
        Error.NotFound("identity.user_not_found", "The user does not exist.");

    public static readonly Error InvalidCredentials =
        Error.Unauthorized("auth.invalid_credentials", "The email or password is incorrect.");

    public static readonly Error EmailTaken =
        Error.Conflict("auth.email_taken", "An account with this email already exists.");

    public static readonly Error GoogleDisabled =
        Error.NotFound("auth.google_disabled", "Sign in with Google is not enabled.");

    public static readonly Error GoogleTokenInvalid =
        Error.Unauthorized("auth.google_token_invalid", "The Google credential is not valid.");

    public static readonly Error GoogleEmailUnverified =
        Error.Unprocessable("auth.google_email_unverified", "The email of the Google account is not verified.");

    public static readonly Error RefreshTokenInvalid =
        Error.Unauthorized("auth.refresh_token_invalid", "The session is not valid. Sign in again.");

    public static readonly Error RefreshTokenExpired =
        Error.Unauthorized("auth.refresh_token_expired", "The session has expired. Sign in again.");

    public static readonly Error RefreshTokenReused =
        Error.Unauthorized("auth.refresh_token_reused", "The session was revoked for security reasons. Sign in again.");

    public static readonly Error RefreshInProgress =
        Error.Conflict("auth.refresh_in_progress", "The session is being refreshed by another request. Retry.");

    public static readonly Error EmailAlreadyVerified =
        Error.Conflict("auth.email_already_verified", "The email address is already verified.");

    public static readonly Error CodeInvalid = Error.Validation("auth.code_invalid", "The code is not valid.");

    public static readonly Error CodeExpired = Error.Validation("auth.code_expired", "The code has expired. Request a new one.");

    public static readonly Error CodeAttemptsExceeded =
        Error.Validation("auth.code_attempts_exceeded", "Too many wrong attempts. Request a new code.");

    public static readonly Error PasswordIncorrect = Error.Validation("auth.password_incorrect", "The password is incorrect.");

    public static readonly Error ReauthenticationRequired =
        Error.Validation("auth.reauthentication_required", "Confirm your identity to continue.");

    public static readonly Error UsernameInvalid =
        Error.Validation("identity.username_invalid", "The username does not follow the username rules.");

    public static readonly Error UsernameReserved = Error.Validation("identity.username_reserved", "This username is reserved.");

    public static readonly Error UsernameTaken = Error.Conflict("identity.username_taken", "This username is already taken.");

    public static readonly Error AccountNotFound = Error.Unauthorized("auth.unauthorized", "The account no longer exists.");

    public static readonly Error EmailDeliveryFailed =
        Error.Unavailable("email.delivery_failed", "The email could not be sent. Try again later.");

    public static Error LockedOut(TimeSpan retryAfter) =>
        Error.Forbidden("auth.locked_out", "Too many failed sign-in attempts. Try again later.", retryAfter);

    public static Error CodeCooldown(TimeSpan retryAfter) =>
        Error.TooManyRequests("auth.code_cooldown", "Wait before requesting another code.", retryAfter);
}
