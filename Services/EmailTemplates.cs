using System.Text.Encodings.Web;

namespace InternTrackAI.Services;

/// <summary>
/// The one place transactional email bodies are written. Deliberately not a templating system: a static
/// method per email, sharing <see cref="Layout"/> so a second email inherits the same shell.
///
/// Rules baked into <see cref="Layout"/>, all of them load-bearing:
/// <list type="bullet">
/// <item>No image, stylesheet or font is loaded from anywhere — no tracking pixel, and nothing to block.
/// Everything visible is text or a table cell with a background colour, so the email reads identically
/// whether or not the client blocks images. The brand dot is a coloured <c>&lt;td&gt;</c> rather than a
/// graphic for that reason; in Outlook's Word engine it squares off, which is the whole degradation.</item>
/// <item>Layout is nested <c>&lt;table&gt;</c> elements, not flexbox or grid, and the card is a fixed 600px
/// (the widest that survives every preview pane) held by both the <c>width</c> attribute and an inline
/// <c>max-width</c>. Outlook reads the attribute, everything else honours the style.</item>
/// <item>Inline styles with literal hex colours, no <c>&lt;style&gt;</c> block. Email clients do not read CSS
/// custom properties and many strip head styles outright, so the <c>site.css</c> tokens cannot be referenced;
/// the values below mirror <c>--accent</c>, <c>--text</c> and <c>--muted</c> by hand. This is the only
/// sanctioned place colours live outside the design system.</item>
/// <item>The markup must never contain the literal <c>http://</c>, so a bare <c>&lt;!doctype html&gt;</c> and
/// no <c>xmlns</c>. Every URL the app emails must be https in production (Railway terminates TLS and the
/// scheme comes from the forwarded headers); <c>ForwardedProtoTests</c> asserts the body has no http:// in it
/// at all, which a doctype or namespace declaration would quietly break.</item>
/// <item>Every email ships an HTML and a plain-text part, and the link appears as raw text in both so a
/// client that strips the button still leaves something the reader can copy.</item>
/// <item>A hidden preheader span opens the body so the inbox preview line is the email's purpose rather than
/// whatever the first paragraph happens to start with.</item>
/// </list>
/// </summary>
public static class EmailTemplates
{
    public const string AppName = "InternTrackAI";

    /// <summary>One line on what the product is, for the footer of every email.</summary>
    private const string Tagline = "InternTrackAI is an AI-assisted tracker for internship and job applications.";

    // Mirrors of the site.css tokens --accent / --text / --text-2 / --muted / --border, inlined for email
    // clients. --card and --surface-2 become plain white and a near-white footer band.
    private const string Accent  = "#0A84FF";
    private const string Text    = "#1D1D1F";
    private const string Text2   = "#48484A";
    private const string Muted   = "#6E6E73";
    private const string Border  = "#E5E5EA";
    private const string Page    = "#F4F4F7";
    private const string Card    = "#FFFFFF";
    private const string Band    = "#FAFAFA";

    /// <summary>The system font stack: no web font, so nothing to download and nothing to fall back from.</summary>
    private const string Font = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    /// <summary>
    /// The password-reset email. <paramref name="lifespan"/> is the real
    /// <c>DataProtectionTokenProviderOptions.TokenLifespan</c>, passed in by the page so the stated expiry
    /// cannot drift away from the token that was actually issued. <paramref name="recipient"/> is the address
    /// the message is going to, echoed in the footer so a reader who did not request this can see which of
    /// their addresses was entered.
    /// </summary>
    public static EmailMessage PasswordReset(string resetUrl, TimeSpan lifespan, string recipient)
    {
        var expiry = Describe(lifespan);

        var html = Layout(
            preheader: $"Reset your {AppName} password",
            heading: "Reset your password",
            intro: $"Someone asked to reset the password for your {AppName} account. Choose a new one here:",
            buttonLabel: "Reset password",
            url: resetUrl,
            note: $"This link expires in {expiry}. If you didn't ask for it you can ignore this email — your password won't change.",
            recipient: recipient,
            reason: "because a password reset was requested for it");

        // Sentences are never hard-wrapped: mail clients wrap plain text themselves, and a manual break
        // mid-sentence shows up as a ragged second line once they do.
        var text = $"""
            Reset your {AppName} password

            Someone asked to reset the password for your {AppName} account. Open this link to choose a new one:

            {resetUrl}

            This link expires in {expiry}. If you didn't ask for it you can ignore this email — your password won't change.

            —
            {Tagline}
            Sent to {recipient} because a password reset was requested for it.
            """;

        return new EmailMessage($"Reset your {AppName} password", html, text);
    }

    /// <summary>
    /// The shared shell: branded header, heading, one line of context, a button-styled link, the raw URL
    /// demoted beneath it, and a footer naming the product and why this address received the mail.
    /// </summary>
    private static string Layout(
        string preheader,
        string heading,
        string intro,
        string buttonLabel,
        string url,
        string note,
        string recipient,
        string reason)
    {
        // Encoded for both the attribute and the visible copy — the URL carries a token straight from Identity.
        var href = HtmlEncoder.Default.Encode(url);
        var e = HtmlEncoder.Default;

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <title>{e.Encode(heading)}</title>
            </head>
            <body style="margin:0;padding:0;background-color:{Page};font-family:{Font};color:{Text};-webkit-text-size-adjust:100%;">

            <span style="display:none;visibility:hidden;opacity:0;color:transparent;height:0;width:0;max-height:0;max-width:0;overflow:hidden;font-size:1px;line-height:1px;">{e.Encode(preheader)}&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;</span>

            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="width:100%;background-color:{Page};">
            <tr>
            <td align="center" style="padding:32px 12px;">

            <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0" style="width:100%;max-width:600px;background-color:{Card};border:1px solid {Border};border-radius:16px;">

            <tr>
            <td style="padding:22px 32px;border-bottom:1px solid {Border};">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
            <td valign="middle" width="10" style="width:10px;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="10" style="width:10px;"><tr>
            <td width="10" height="10" bgcolor="{Accent}" style="width:10px;height:10px;background-color:{Accent};border-radius:50%;font-size:0;line-height:10px;">&nbsp;</td>
            </tr></table>
            </td>
            <td valign="middle" width="10" style="width:10px;font-size:0;line-height:0;">&nbsp;</td>
            <td valign="middle" style="font-family:{Font};font-size:16px;font-weight:600;letter-spacing:-0.01em;color:{Text};white-space:nowrap;">{AppName}</td>
            </tr></table>
            </td>
            </tr>

            <tr>
            <td style="padding:32px 32px 8px;">
            <h1 style="margin:0 0 12px;font-family:{Font};font-size:24px;line-height:1.3;font-weight:600;letter-spacing:-0.02em;color:{Text};">{e.Encode(heading)}</h1>
            <p style="margin:0 0 28px;font-family:{Font};font-size:15px;line-height:1.6;color:{Text2};">{e.Encode(intro)}</p>

            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 28px;"><tr>
            <td align="center" bgcolor="{Accent}" style="background-color:{Accent};border-radius:10px;">
            <a href="{href}" style="display:inline-block;padding:14px 32px;font-family:{Font};font-size:16px;font-weight:600;line-height:1;color:#FFFFFF;text-decoration:none;border-radius:10px;">{e.Encode(buttonLabel)}</a>
            </td>
            </tr></table>

            <p style="margin:0 0 4px;font-family:{Font};font-size:12px;line-height:1.5;color:{Muted};">Button not working? Paste this link:</p>
            <p style="margin:0 0 24px;font-family:{Font};font-size:12px;line-height:1.5;color:{Muted};word-break:break-all;overflow-wrap:anywhere;"><a href="{href}" style="color:{Muted};text-decoration:underline;">{href}</a></p>

            <p style="margin:0 0 4px;padding-top:20px;border-top:1px solid {Border};font-family:{Font};font-size:13px;line-height:1.6;color:{Text2};">{e.Encode(note)}</p>
            </td>
            </tr>

            <tr>
            <td style="padding:20px 32px 24px;background-color:{Band};border-top:1px solid {Border};border-radius:0 0 16px 16px;">
            <p style="margin:0 0 6px;font-family:{Font};font-size:12px;line-height:1.5;color:{Muted};">{e.Encode(Tagline)}</p>
            <p style="margin:0;font-family:{Font};font-size:12px;line-height:1.5;color:{Muted};">Sent to {e.Encode(recipient)} {e.Encode(reason)}.</p>
            </td>
            </tr>

            </table>

            </td>
            </tr>
            </table>

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
