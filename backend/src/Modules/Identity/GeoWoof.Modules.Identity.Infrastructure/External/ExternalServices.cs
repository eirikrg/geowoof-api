using System.ComponentModel.DataAnnotations;
using GeoWoof.Modules.Identity.Application.Abstractions;
using Google.Apis.Auth;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GeoWoof.Modules.Identity.Infrastructure.External;

/// <summary>Sign in with Google (configuration section <c>Auth:Google</c>). Disabled while no client id is configured.</summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "Auth:Google";

    /// <summary>Accepted audiences: the OAuth "Web application" client id used by the app as server client id.</summary>
    public string[] ClientIds { get; set; } = [];
}

/// <summary>Validates Google ID tokens: signature (Google's public keys), issuer, expiry and audience.</summary>
internal sealed class GoogleIdTokenValidator(IOptions<GoogleAuthOptions> options) : IGoogleIdTokenValidator
{
    private readonly string[] _clientIds = options.Value.ClientIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();

    public bool IsEnabled => _clientIds.Length > 0;

    public async Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null;
        }

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = _clientIds });
            return new GoogleIdentity(payload.Subject, payload.Email, payload.EmailVerified, payload.Name);
        }
        catch (InvalidJwtException)
        {
            return null;
        }
    }
}

/// <summary>Outgoing email over SMTP (configuration section <c>Email</c>; Mailpit in local development).</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = "no-reply@geowoof.local";

    [Required]
    public string FromName { get; set; } = "GeoWoof";

    [Required]
    public string SmtpHost { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    /// <summary><c>None</c> (Mailpit), <c>StartTls</c> or <c>SslOnConnect</c>.</summary>
    public SecureSocketOptions SmtpSecurity { get; set; } = SecureSocketOptions.StartTls;

    public string? SmtpUserName { get; set; }

    public string? SmtpPassword { get; set; }
}

internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;

        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, settings.SmtpSecurity, cancellationToken);
        if (!string.IsNullOrEmpty(settings.SmtpUserName))
        {
            await client.AuthenticateAsync(settings.SmtpUserName, settings.SmtpPassword ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
