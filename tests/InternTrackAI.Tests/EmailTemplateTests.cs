using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The password-reset email body. Most of these pin constraints that are invisible until an email lands
/// badly somewhere: no external resource to block or track, no <c>http://</c> anywhere (which
/// <c>ForwardedProtoTests</c> asserts from the far end of the flow, where the failure is much harder to
/// read), and an expiry sentence that follows the token rather than a number typed into the copy.
/// </summary>
public class EmailTemplateTests
{
    private const string Url = "https://interntrackai.example/Identity/Account/ResetPassword?code=CfDJ8ABC%2Fdef%2B123";
    private const string Recipient = "reader@example.test";

    private static EmailMessage Reset(TimeSpan? lifespan = null) =>
        EmailTemplates.PasswordReset(Url, lifespan ?? TimeSpan.FromDays(1), Recipient);

    [Fact]
    public void The_reset_email_names_the_app_explains_why_and_offers_a_way_out()
    {
        var message = Reset();

        Assert.Contains("InternTrackAI", message.Subject);
        foreach (var body in new[] { message.Html, message.Text! })
        {
            Assert.Contains("InternTrackAI", body);
            Assert.Contains("reset the password", body);
            Assert.Contains("ignore this email", body);
        }

        // The reassurance line reads the same in both, but the HTML part has its apostrophe encoded.
        Assert.Contains("password won&#x27;t change", message.Html);
        Assert.Contains("password won't change", message.Text);
    }

    [Fact]
    public void The_link_is_a_button_and_a_plain_url_in_the_html_and_a_bare_url_in_the_text()
    {
        var message = Reset();

        // Twice in the HTML: once as the button's href, once as copyable text for clients that strip buttons.
        Assert.Contains("Reset password</a>", message.Html);
        Assert.Contains("Button not working? Paste this link:", message.Html);
        Assert.Equal(3, CountOccurrences(message.Html, Url));   // button href + the plain line's href and label

        // The text part carries the URL with nothing wrapped around it.
        Assert.Contains(Url, message.Text);
        Assert.DoesNotContain("<", message.Text);
    }

    [Fact]
    public void The_expiry_line_follows_the_real_token_lifespan()
    {
        Assert.Contains("expires in 24 hours", Reset(TimeSpan.FromDays(1)).Html);
        Assert.Contains("expires in 24 hours", Reset(TimeSpan.FromDays(1)).Text);
        Assert.Contains("expires in 2 hours", Reset(TimeSpan.FromHours(2)).Html);
        Assert.Contains("expires in 1 hour", Reset(TimeSpan.FromHours(1)).Html);
        Assert.Contains("expires in 30 minutes", Reset(TimeSpan.FromMinutes(30)).Html);
        Assert.Contains("expires in 90 minutes", Reset(TimeSpan.FromMinutes(90)).Html);
    }

    [Fact]
    public void The_email_never_contains_a_plain_http_url()
    {
        // ForwardedProtoTests asserts this from the other end of the flow; a doctype or an xmlns
        // declaration would break it there with a far less obvious message than here.
        var message = Reset();
        Assert.DoesNotContain("http://", message.Html);
        Assert.DoesNotContain("http://", message.Text);
    }

    [Fact]
    public void The_email_loads_nothing_from_anywhere()
    {
        var html = Reset().Html;
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("src=", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("background-image", html);
        Assert.DoesNotContain("@import", html);
    }

    [Fact]
    public void The_fallback_link_is_demoted_below_the_button()
    {
        var html = Reset().Html;

        // The button is the prominent element: bigger type, white on accent. The raw URL beneath it is
        // small, muted and breakable, so it reads as a fallback rather than a second call to action.
        Assert.Contains($"font-size:16px;font-weight:600;line-height:1;color:#FFFFFF;text-decoration:none", html);
        Assert.Contains("word-break:break-all;overflow-wrap:anywhere;", html);

        // Whatever the exact rules, the URL line must never end up styled larger than the button's label.
        var buttonSize = FontSizeAt(html, html.IndexOf("Reset password</a>", StringComparison.Ordinal));
        var urlSize = FontSizeAt(html, html.IndexOf("Button not working?", StringComparison.Ordinal));
        Assert.True(urlSize < buttonSize, $"fallback URL ({urlSize}px) should be smaller than the button ({buttonSize}px)");
    }

    [Fact]
    public void The_inbox_preview_line_is_the_emails_purpose_and_is_not_visible_in_the_body()
    {
        var html = Reset().Html;

        // The preheader sits immediately after <body> so it becomes the snippet, and is hidden every way
        // a mail client might measure: no display, no size, transparent.
        // Followed by a run of zero-width characters, which pushes the body's first line out of the snippet.
        var preheader = html.IndexOf("Reset your InternTrackAI password&#847;&zwnj;", StringComparison.Ordinal);
        Assert.True(preheader > 0, "preheader span is missing");
        Assert.True(preheader < html.IndexOf("<h1", StringComparison.Ordinal), "preheader must precede the heading");
        Assert.Contains("display:none;visibility:hidden;opacity:0;color:transparent;", html);
    }

    [Fact]
    public void The_footer_says_what_the_app_is_and_why_this_address_got_the_email()
    {
        var message = Reset();

        foreach (var body in new[] { message.Html, message.Text! })
        {
            Assert.Contains("AI-assisted tracker for internship and job applications", body);
            Assert.Contains($"Sent to {Recipient} because a password reset was requested for it", body);
        }
    }

    [Fact]
    public void The_recipient_address_is_encoded_into_the_footer()
    {
        // The address is whatever was typed into an anonymous form; it reaches the markup as data.
        var message = EmailTemplates.PasswordReset(Url, TimeSpan.FromDays(1), "a<b>@example.test");

        Assert.Contains("a&lt;b&gt;@example.test", message.Html);
        Assert.DoesNotContain("<b>", message.Html);
    }

    [Fact]
    public void The_layout_is_tables_and_inline_styles_so_it_survives_outlook()
    {
        var html = Reset().Html;

        Assert.Contains("<table role=\"presentation\"", html);
        Assert.Contains("width=\"600\"", html);       // Outlook reads the attribute...
        Assert.Contains("max-width:600px", html);     // ...everything else the style
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("display:flex", html);
        Assert.DoesNotContain("display:grid", html);
        Assert.DoesNotContain("var(--", html);

        // The brand dot is a coloured cell, not a graphic, so it is there with images blocked.
        // Matched as "some hex", not a specific one: the accent moved once already for contrast
        // (#0A84FF -> #0066CC, white-on-accent was 3.65:1), and pinning the literal here only
        // produces a failing test that says nothing about whether the dot still renders.
        var bgcolors = System.Text.RegularExpressions.Regex.Matches(html, "bgcolor=\"(#[0-9A-Fa-f]{6})\"")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(bgcolors);
        // The dot and the call-to-action button are both the accent, so the brand colour is one value.
        Assert.Single(bgcolors.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The <c>font-size:NNpx</c> governing the text at <paramref name="index"/> — the nearest one before it.</summary>
    private static int FontSizeAt(string html, int index)
    {
        var at = html.LastIndexOf("font-size:", index, StringComparison.Ordinal);
        Assert.True(at > 0, "no font-size found before the given text");
        var digits = new string(html.Skip(at + "font-size:".Length).TakeWhile(char.IsDigit).ToArray());
        return int.Parse(digits);
    }

    [Fact]
    public void The_url_is_html_encoded_in_the_markup_but_intact_in_the_text()
    {
        var tricky = "https://x.test/Reset?code=a&b=c<d";
        var message = EmailTemplates.PasswordReset(tricky, TimeSpan.FromDays(1), Recipient);

        Assert.Contains("a&amp;b=c&lt;d", message.Html);
        Assert.DoesNotContain("c<d", message.Html);
        Assert.Contains(tricky, message.Text);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
