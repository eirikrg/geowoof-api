namespace GeoWoof.Modules.Identity.Presentation;

// Request bodies of the Identity endpoints. Responses are the Application DTOs.

public sealed record ReplaceProfileRequest(string DisplayName, string? Bio, string? City, Guid? AvatarAssetId);

public sealed record RegisterRequest(string Email, string Password, string DisplayName, string? Language);

public sealed record LoginRequest(string Email, string Password);

public sealed record GoogleSignInRequest(string IdToken, string? Language);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? RefreshToken, bool? AllDevices);

public sealed record VerifyEmailRequest(string Code);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Code, string NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string NewPassword);

public sealed record UpdateMeRequest(string? DisplayName, string? Language);

public sealed record ChangeUsernameRequest(string Username, bool? IsPrivate = null);

public sealed record UpdateSettingsRequest(string? LiveVisibility, int? HideStartDistanceM);

public sealed record ChangePrivacyRequest(bool IsPrivate);

public sealed record DeleteAccountRequest(string? Password, string? GoogleIdToken);
