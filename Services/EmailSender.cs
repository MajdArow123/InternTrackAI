using Microsoft.AspNetCore.Identity.UI.Services;

namespace InternTrackAI.Services;

/// <summary>
/// Development-only <see cref="IEmailSender"/> implementation used by ASP.NET Core Identity
/// (account confirmation, password reset, etc.). Instead of sending a real email, it writes the
/// message to the application log so developers can copy confirmation/reset links during local
/// testing without configuring an SMTP provider.
/// </summary>
public class ConsoleEmailSender : IEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _logger;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// "Sends" an email by logging its contents instead of dispatching it over a real transport.
    /// </summary>
    /// <param name="email">Recipient address (logged only, not validated or used to send anything).</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="htmlMessage">HTML body, typically containing a confirmation or reset link.</param>
    /// <returns>A completed task — there is no real I/O to await.</returns>
    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("📧 EMAIL TO: {Email}", email);
        _logger.LogInformation("📨 SUBJECT: {Subject}", subject);
        _logger.LogInformation("───────────────────────────────────────────────────────────");
        _logger.LogInformation("{Message}", htmlMessage);
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        return Task.CompletedTask;
    }
}
