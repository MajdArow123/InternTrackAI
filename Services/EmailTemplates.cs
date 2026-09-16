using System.Text.Encodings.Web;

namespace InternTrackAI.Services;

/// <summary>
/// The one place transactional email bodies are written. Deliberately not a templating system: a static
/// method per email, sharing <see cref="Layout"/> so a second email inherits the same shell.
///
/// Rules baked into <see cref="Layout"/>, all of them load-bearing:
/// <list type="bullet">
/// <item>No image, stylesheet or font is loaded from anywhere — no tracking pixel, and nothing to block.</item>
/// <item>Inline styles with literal hex colours. Email clients do not read CSS custom properties, so the
/// <c>site.css</c> tokens cannot be referenced; the values below mirror <c>--accent</c>, <c>--text</c> and
/// <c>--muted</c> by hand. This is the only sanctioned place colours live outside the design system.</item>
/// <item>The markup must never contain the literal <c>http://</c>, so a bare <c>&lt;!doctype html&gt;</c> and
/// no <c>xmlns</c>. Every URL the app emails must be https in production (Railway terminates TLS and the
/// scheme comes from the forwarded headers); <c>ForwardedProtoTests</c> asserts the body has no http:// in it
/// at all, which a doctype or namespace declaration would quietly break.</item>
/// <item>Every email ships an HTML and a plain-text part, and the link appears as raw text in both so a
/// client that strips the button still leaves something the reader can copy.</item>
/// </list>
/// </summary>
public static class EmailTemplates
{
    public const string AppName = "InternTrackAI";

    // Mirrors of the site.css tokens --accent / --text / --muted / --border, inlined for email clients.
    private const string Accent = "#0A84FF";
    private const string Text   = "#1D1D1F";
    private const string Muted  = "#6E6E73";
    private const string Border = "#E5E5EA";

    /// <summary>
    /// The password-reset email. <paramref name="lifespan"/> is the real
    /// <c>DataProtectionTokenProviderOptions.TokenLifespan</c>, passed in by the page so the stated expiry
    /// cannot drift away from the token that was actually issued.
    /// </summary>
    public static EmailMessage PasswordReset(string resetUrl, TimeSpan lifespan)
    {
        var expiry = Describe(lifespan);

        var html = Layout(
            heading: "Reset your password",
            intro: $"Someone asked to reset the password for your {AppName} account. Choose a new one here:",
            buttonLabel: "Reset password",
            url: resetUrl,
            footer: $"This link expires in {expiry}. If you didn't ask for it you can ignore this email — your password won't change.");

        // Sentences are never hard-wrapped: mail clients wrap plain text themselves, and a manual break
        // mid-sentence shows up as a ragged second line once they do.
        var text = $"""
            Reset your {AppName} password

            Someone asked to reset the password for your {AppName} account. Open this link to choose a new one:

            {resetUrl}

            This link expires in {expiry}. If you didn't ask for it you can ignore this email — your password won't change.
            """;

        return new EmailMessage($"Reset your {AppName} password", html, text);
    }

    /// <summary>The shared shell: heading, one line of context, a button-styled link, the raw URL, a footer note.</summary>
    private static string Layout(string heading, string intro, string buttonLabel, string url, string footer)
    {
        // Encoded for both the attribute and the visible copy — the URL carries a token straight from Identity.
        var href = HtmlEncoder.Default.Encode(url);

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{HtmlEncoder.Default.Encode(heading)}</title>
            </head>
            <body style="margin:0;padding:24px;background:#F4F4F7;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:{Text};">
            <div style="max-width:520px;margin:0 auto;background:#FFFFFF;border:1px solid {Border};border-radius:16px;padding:32px;">
            <p style="margin:0 0 24px;font-size:15px;font-weight:600;color:{Text};">{AppName}</p>
            <h1 style="margin:0 0 12px;font-size:22px;line-height:1.3;font-weight:600;color:{Text};">{HtmlEncoder.Default.Encode(heading)}</h1>
            <p style="margin:0 0 24px;font-size:15px;line-height:1.6;color:{Muted};">{HtmlEncoder.Default.Encode(intro)}</p>
            <p style="margin:0 0 24px;">
            <a href="{href}" style="display:inline-block;padding:12px 24px;background:{Accent};color:#FFFFFF;text-decoration:none;border-radius:10px;font-size:15px;font-weight:600;">{HtmlEncoder.Default.Encode(buttonLabel)}</a>
            </p>
            <p style="margin:0 0 8px;font-size:13px;line-height:1.5;color:{Muted};">Or paste this link into your browser:</p>
            <p style="margin:0 0 24px;font-size:13px;line-height:1.5;word-break:break-all;"><a href="{href}" style="color:{Accent};">{href}</a></p>
            <p style="margin:0;padding-top:20px;border-top:1px solid {Border};font-size:13px;line-height:1.6;color:{Muted};">{HtmlEncoder.Default.Encode(footer)}</p>
            </div>
            </body>
            </html>
            """;
    }

    /// <summary>"24 hours", "2 hours", "30 minutes" — whichever unit states the lifespan without a fraction.</summary>
    private static string Describe(TimeSpan lifespan)
    {
        if (lifespan.TotalHours >= 1 && lifespan.TotalHours == Math.Floor(lifespan.TotalHours))
        {
            var hours = (int)lifespan.TotalHours;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        var minutes = Math.Max(1, (int)Math.Round(lifespan.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
