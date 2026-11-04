using System.Globalization;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application.EmailVerification;

/// <summary>Plain-text emails of the Identity module, in the user's language.</summary>
internal static class IdentityEmails
{
    public static EmailMessage EmailVerification(User user, string code, TimeSpan lifetime)
    {
        var minutes = ((int)lifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        return user.Language == Language.En
            ? new EmailMessage(
                user.Email.Value,
                $"Your GeoWoof verification code: {code}",
                $"Hi {user.DisplayName},\n\nYour code to verify your email address is {code}. It expires in {minutes} minutes.\n\n"
                + "If you did not create a GeoWoof account, you can ignore this email.\n\nGeoWoof")
            : new EmailMessage(
                user.Email.Value,
                $"Tu código de verificación de GeoWoof: {code}",
                $"Hola, {user.DisplayName}:\n\nTu código para verificar tu email es {code}. Caduca en {minutes} minutos.\n\n"
                + "Si no has creado una cuenta en GeoWoof, ignora este mensaje.\n\nGeoWoof");
    }

    public static EmailMessage PasswordReset(User user, string code, TimeSpan lifetime)
    {
        var minutes = ((int)lifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        return user.Language == Language.En
            ? new EmailMessage(
                user.Email.Value,
                $"Your GeoWoof password reset code: {code}",
                $"Hi {user.DisplayName},\n\nYour code to reset your password is {code}. It expires in {minutes} minutes.\n\n"
                + "If you did not ask to reset your password, you can ignore this email: your password has not changed.\n\nGeoWoof")
            : new EmailMessage(
                user.Email.Value,
                $"Tu código para restablecer la contraseña de GeoWoof: {code}",
                $"Hola, {user.DisplayName}:\n\nTu código para restablecer la contraseña es {code}. Caduca en {minutes} minutos.\n\n"
                + "Si no lo has pedido tú, ignora este mensaje: tu contraseña no ha cambiado.\n\nGeoWoof");
    }
}
