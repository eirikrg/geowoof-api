using GeoWoof.Modules.Identity.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace GeoWoof.Modules.Identity.Application.EmailVerification;

/// <summary>
/// Sends Identity emails after the unit of work is saved. Delivery failures are logged and reported
/// as <c>false</c>: the caller decides whether that fails the request (resend) or not (registration).
/// </summary>
internal sealed partial class IdentityEmailDispatcher(IEmailSender emailSender, ILogger<IdentityEmailDispatcher> logger)
{
    public async Task<bool> TrySendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await emailSender.SendAsync(message, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDeliveryFailed(logger, exception, message.Subject);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not send the email '{Subject}'")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, string subject);
}
