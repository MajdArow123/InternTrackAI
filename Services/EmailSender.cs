using Microsoft.AspNetCore.Identity.UI.Services;

namespace InternTrackAI.Services;

/// <summary>
/// One email in both representations. <paramref name="Text"/> is optional: Resend derives a plain-text part
/// from the HTML when it is omitted, which is what the compiled Identity UI pages (they only ever hand over
/// an HTML string) end up relying on.
/// </summary>
public sealed record EmailMessage(string Subject, string Html, string? Text = null);

/// <summary>
/// The app's own email seam. Extends Identity's <see cref="IEmailSender"/> — which the compiled Identity UI
/// pages resolve directly, so it has to stay registered — with a plain-text alternative and a cancellation
/// token. Implementations never throw when delivery fails: the caller's response must not depend on whether
/// the message got out (see <c>ForgotPassword</c>, where it would otherwise leak whether an account exists).
/// </summary>
public interface IAppEmailSender : IEmailSender
{
    Task SendAsync(string to, EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// The fallback <see cref="IAppEmailSender"/>, used whenever <c>Resend:ApiKey</c> is unset — normally local
/// development. Instead of sending anything it writes the message to the application log, so a developer can
/// copy the reset link out of the console without configuring a mail provider. <c>Program.cs</c> picks
/// between this and <see cref="ResendEmailSender"/> at startup and logs which one is live.
/// </summary>
public class ConsoleEmailSender : IAppEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _logger;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger)
    {
        _logger = logger;
    }

    /// <summary>Identity's three-argument entry point; forwards to <see cref="SendAsync"/> with no text part.</summary>
    public Task SendEmailAsync(string email, string subject, string htmlMessage) =>
        SendAsync(email, new EmailMessage(subject, htmlMessage));

    /// <summary>"Sends" an email by logging it instead of dispatching it over a real transport.</summary>
    public Task SendAsync(string to, EmailMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("📧 EMAIL TO: {Email}", to);
        _logger.LogInformation("📨 SUBJECT: {Subject}", message.Subject);
        _logger.LogInformation("───────────────────────────────────────────────────────────");
        _logger.LogInformation("{Message}", message.Html);
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            _logger.LogInformation("─────────────────────────── text ──────────────────────────");
            _logger.LogInformation("{Text}", message.Text);
        }
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        return Task.CompletedTask;
    }
}
