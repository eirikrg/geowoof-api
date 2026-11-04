using System.Text.RegularExpressions;

namespace GeoWoof.Modules.Identity.Domain.Users;

/// <summary>An email address, normalised (trimmed and lower-cased) so that it is unique case-insensitively.</summary>
public sealed partial record Email
{
    public const int MaxLength = 254;

    private Email(string value)
    {
        Value = value;
    }

    public string Value { get; }

    /// <summary>The part before the <c>@</c>, used as a default display name.</summary>
    public string LocalPart => Value[..Value.IndexOf('@', StringComparison.Ordinal)];

    public static string Normalize(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValid(string? raw)
    {
        var normalized = Normalize(raw);
        return normalized.Length is > 0 and <= MaxLength && Shape().IsMatch(normalized);
    }

    /// <summary>Creates a normalised email. Callers validate first; an invalid value is a programming error.</summary>
    public static Email Create(string raw) =>
        IsValid(raw) ? new Email(Normalize(raw)) : throw new ArgumentException("The email address is not valid.", nameof(raw));

    /// <summary>Restores a value that was validated before it was stored.</summary>
    public static Email FromStorage(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
