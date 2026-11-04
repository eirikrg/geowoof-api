using FluentValidation;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application;

/// <summary>Field rules shared by the Identity validators (documented in docs/api.md).</summary>
internal static class IdentityValidationRules
{
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;
    public const int CodeLength = 6;

    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Must(Email.IsValid).WithMessage("Enter a valid email address.");

    public static IRuleBuilderOptions<T, string> ValidPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Length(PasswordMinLength, PasswordMaxLength)
            .Must(password => password.Any(char.IsLetter) && password.Any(char.IsDigit))
            .WithMessage("The password must contain at least one letter and one digit.");

    public static IRuleBuilderOptions<T, string> ValidDisplayName<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(User.IsValidDisplayName).WithMessage($"The name must have 1 to {User.DisplayNameMaxLength} characters.");

    public static IRuleBuilderOptions<T, string> ValidUsernameFormat<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(username => Username.Check(username) != UsernameCheck.Invalid)
            .WithMessage($"Use {Username.MinLength}-{Username.MaxLength} characters: letters, digits, '_' and '.', starting with a letter.");

    public static IRuleBuilderOptions<T, string?> SupportedLanguage<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(language => language is null || LanguageCodes.IsSupported(language)).WithMessage("Use 'es' or 'en'.");

    public static IRuleBuilderOptions<T, string> ValidCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Length(CodeLength).Must(code => code.All(char.IsAsciiDigit)).WithMessage("The code has 6 digits.");
}
