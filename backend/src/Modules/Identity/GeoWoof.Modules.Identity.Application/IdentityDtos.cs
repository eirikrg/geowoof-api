using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application;

/// <summary>The account of the signed-in user, as returned by the API.</summary>
public sealed record UserDto(
    Guid Id,
    string Email,
    bool EmailVerified,
    string? Username,
    string DisplayName,
    bool HasPassword,
    IReadOnlyList<string> SignInMethods,
    string Language,
    DateTimeOffset CreatedAt)
{
    public const string PasswordMethod = "Password";
    public const string GoogleMethod = "Google";

    internal static UserDto From(User user)
    {
        var methods = new List<string>(2);
        if (user.HasPassword)
        {
            methods.Add(PasswordMethod);
        }

        if (user.GoogleSubject is not null)
        {
            methods.Add(GoogleMethod);
        }

        return new UserDto(
            user.Id,
            user.Email.Value,
            user.EmailVerified,
            user.Username?.Value,
            user.DisplayName,
            user.HasPassword,
            methods,
            LanguageCodes.ToCode(user.Language),
            user.CreatedAt);
    }
}

public sealed record SessionDto(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    bool IsNewUser,
    UserDto User);

public sealed record AccessTokenDto(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);

public sealed record AuthProvidersDto(bool Google);

public enum UsernameUnavailableReason
{
    Taken = 0,
    Reserved = 1,
    Invalid = 2,
}

public sealed record UsernameAvailabilityDto(string Username, bool Available, UsernameUnavailableReason? Reason);

/// <summary>Language codes of the API (<c>es</c>, <c>en</c>).</summary>
public static class LanguageCodes
{
    public const string Spanish = "es";
    public const string English = "en";

    public static bool IsSupported(string? code) =>
        string.Equals(code, Spanish, StringComparison.OrdinalIgnoreCase) || string.Equals(code, English, StringComparison.OrdinalIgnoreCase);

    /// <summary>Spanish when the code is missing or unsupported.</summary>
    public static Language Parse(string? code) =>
        string.Equals(code, English, StringComparison.OrdinalIgnoreCase) ? Language.En : Language.Es;

    public static string ToCode(Language language) => language == Language.En ? English : Spanish;
}
