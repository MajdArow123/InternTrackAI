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

    private static EmailMessage Reset(TimeSpan? lifespan = null) =>
        EmailTemplates.PasswordReset(Url, lifespan ?? TimeSpan.FromDays(1));

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
        Assert.Contains("Or paste this link into your browser", message.Html);
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
    public void The_url_is_html_encoded_in_the_markup_but_intact_in_the_text()
    {
        var tricky = "https://x.test/Reset?code=a&b=c<d";
        var message = EmailTemplates.PasswordReset(tricky, TimeSpan.FromDays(1));

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
