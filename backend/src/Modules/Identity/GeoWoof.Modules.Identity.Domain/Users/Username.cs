using System.Collections.Frozen;
using System.Text.RegularExpressions;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Identity.Domain.Users;

/// <summary>Result of checking a candidate username against the rules.</summary>
public enum UsernameCheck
{
    Valid = 0,
    Invalid = 1,
    Reserved = 2,
}

/// <summary>
/// A unique public handle (<c>@ana.paws</c>): 3–30 characters, starts with a letter, only
/// <c>a-z 0-9 _ .</c>, no <c>..</c> and no trailing dot. Case-insensitive (stored lower-cased).
/// </summary>
public sealed partial record Username
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    private static readonly FrozenSet<string> ReservedNames = new[]
    {
        "about", "account", "accounts", "admin", "administrator", "anonymous", "api", "app", "contact",
        "explore", "geo_woof", "geowoof", "help", "helpdesk", "info", "login", "logout", "mail", "me",
        "messages", "mod", "moderator", "notifications", "null", "official", "privacy", "profile",
        "register", "root", "search", "security", "settings", "signin", "signup", "staff", "support",
        "system", "team", "terms", "undefined", "user", "users", "www",
    }.ToFrozenSet(StringComparer.Ordinal);

    private Username(string value)
    {
        Value = value;
    }

    public string Value { get; }

    /// <summary>Lower-cases, trims and drops one leading <c>@</c>.</summary>
    public static string Normalize(string? raw)
    {
        var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    public static UsernameCheck Check(string? raw)
    {
        var normalized = Normalize(raw);
        if (normalized.Length is < MinLength or > MaxLength
            || !Shape().IsMatch(normalized)
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.EndsWith('.'))
        {
            return UsernameCheck.Invalid;
        }

        return ReservedNames.Contains(normalized) ? UsernameCheck.Reserved : UsernameCheck.Valid;
    }

    public static Result<Username> Create(string? raw) => Check(raw) switch
    {
        UsernameCheck.Valid => new Username(Normalize(raw)),
        UsernameCheck.Reserved => IdentityErrors.UsernameReserved,
        _ => IdentityErrors.UsernameInvalid,
    };

    /// <summary>Restores a value that was validated before it was stored.</summary>
    public static Username FromStorage(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
