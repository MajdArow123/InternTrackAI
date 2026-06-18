using Microsoft.AspNetCore.Identity.UI.Services;

namespace InternTrackAI.Services;

public class ConsoleEmailSender : IEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _logger;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger)
    {
        _logger = logger;
    }

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
