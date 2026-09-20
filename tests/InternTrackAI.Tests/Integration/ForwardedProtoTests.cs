using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Railway terminates TLS and forwards plain HTTP with X-Forwarded-Proto/-Host. Every absolute URL the
/// app hands to the outside world (Google's redirect_uri, e-mailed reset links, the bookmarklet, the
/// calendar feed) must therefore come out as https once those headers are
/// present. The test host itself speaks http://localhost, exactly like the container behind the proxy.
///
/// The in-process test server reports no remote IP, and the forwarded-headers middleware trusts the
/// first hop whenever the remote IP is unknown, so on its own it would accept the headers even with the
/// default loopback-only trust list (the production bug). <see cref="BehindProxy"/> therefore stamps a
/// non-loopback proxy address on every connection so the KnownNetworks/KnownProxies rules really apply.
/// </summary>
public class ForwardedProtoTests
{
    private const string PublicHost = "interntrackai.example";
    private static readonly IPAddress ProxyAddress = IPAddress.Parse("100.64.12.34");

    /// <summary>Runs before the app's pipeline and makes every connection look like it came from Railway's proxy.</summary>
    private sealed class FakeProxyConnection : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = ProxyAddress;
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    private static WebApplicationFactory<Program> BehindProxy(WebApplicationFactory<Program> factory, Action<IServiceCollection>? services = null) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IStartupFilter, FakeProxyConnection>();
            services?.Invoke(s);
        }));

    /// <summary>Captures what Identity would have e-mailed instead of logging it.</summary>
    private sealed class CapturingEmailSender : IAppEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();

        public Task SendEmailAsync(string email, string subject, string htmlMessage) =>
            SendAsync(email, new EmailMessage(subject, htmlMessage));

        public Task SendAsync(string to, EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add((to, message.Subject, message.Html));
            return Task.CompletedTask;
        }
    }

    /// <summary>A request as Railway's proxy would deliver it: plain http to the container, public origin in the headers.</summary>
    private static HttpRequestMessage Proxied(HttpMethod method, string path, HttpContent? content = null) =>
        ProxiedFrom(PublicHost, method, path, content);

    /// <summary>The same, for a named public host — the app answers on more than one.</summary>
    private static HttpRequestMessage ProxiedFrom(string host, HttpMethod method, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", host);
        req.Headers.Add("X-Forwarded-For", "10.0.0.7");
        return req;
    }

    private static string RedirectUriOf(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith("https://accounts.google.test/", location);
        return HttpUtility.ParseQueryString(new Uri(location).Query)["redirect_uri"]!;
    }

    /// <summary>
    /// The app is served on two public hostnames at once — the custom domain and the original Railway one,
    /// which is kept alive so existing links and bookmarks don't break. Nothing may pin either: every
    /// absolute URL has to follow the host the request actually arrived on, in the same process, with no
    /// restart in between. Hard-coding a host, or setting <c>Google:RedirectBaseUrl</c>, breaks this.
    /// </summary>
    [Theory]
    [InlineData("interntrackai.majdarow.com")]
    [InlineData("interntrackai-production.up.railway.app")]
    public async Task Every_absolute_url_follows_the_host_the_request_arrived_on(string host)
    {
        var emails = new CapturingEmailSender();
        var (parent, inner, oauth, _) = GmailTestHost.Boot();
        using var _ = parent; using var __ = inner;
        using var factory = BehindProxy(inner, s =>
        {
            s.RemoveAll<IAppEmailSender>();
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IAppEmailSender>(emails);
            s.AddSingleton<IEmailSender>(emails);
        });
        var client = GmailTestHost.Client(factory);
        var email = await Http.RegisterAsync(client);

        // Gmail's OAuth redirect_uri.
        Assert.Equal($"https://{host}/Integrations/Gmail/Callback",
            RedirectUriOf(await client.SendAsync(ProxiedFrom(host, HttpMethod.Get, "/Integrations/Gmail/Connect"))));

        // The bookmarklet's target origin and the calendar feed URL, both rendered into the profile page.
        var bookmarklet = await client.SendAsync(ProxiedFrom(host, HttpMethod.Get, "/Profile/Bookmarklet"));
        Assert.Contains($"window.open('https://{host}/Capture?url='",
            HttpUtility.HtmlDecode(await bookmarklet.Content.ReadAsStringAsync()));

        var profile = await client.SendAsync(ProxiedFrom(host, HttpMethod.Get, "/Profile"));
        Assert.Matches($"https://{Regex.Escape(host)}/Calendar/feed\\.ics\\?token=", await profile.Content.ReadAsStringAsync());

        // And the emailed reset link.
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/ForgotPassword");
        await client.SendAsync(ProxiedFrom(host, HttpMethod.Post, "/Identity/Account/ForgotPassword",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["__RequestVerificationToken"] = token,
            })));
        var (_, _, body) = Assert.Single(emails.Sent);
        Assert.Contains($"https://{host}/Identity/Account/ResetPassword?code=", body);
        Assert.DoesNotContain("http://", body);
    }

    /// <summary>
    /// <c>ForwardLimit = 1</c> means the middleware consumes exactly one X-Forwarded-For entry, and it takes
    /// the <b>rightmost</b> one — the address the nearest proxy appended. Anything the client put in the
    /// header therefore sits to its left and is thrown away. That is the whole reason a client cannot reset
    /// <see cref="RegistrationLimiter"/> or <see cref="PasswordResetLimiter"/> at will by inventing an
    /// address, so it is pinned behaviourally rather than by reading the option back: raise the limit to
    /// more than one hop and the key becomes a value the caller chose, and these assertions flip.
    ///
    /// <para>The registration limiter is the oracle because it is the one limit whose refusal is visible —
    /// ForgotPassword answers a refusal with the same page as a success, on purpose.</para>
    /// </summary>
    [Fact]
    public async Task Only_the_last_forwarded_for_entry_is_trusted()
    {
        using var parent = new TestAppFactory();
        using var factory = BehindProxy(parent.WithWebHostBuilder(b =>
            b.UseSetting("RateLimiting:Registration:PerIpPerHour", "1")));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        async Task<HttpStatusCode> RegisterFrom(string forwardedFor)
        {
            var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/ForgotPassword");
            var req = new HttpRequestMessage(HttpMethod.Post, "/Identity/Account/Register")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Input.Email"]           = $"fwd-{Guid.NewGuid():N}@example.test",
                    ["Input.Password"]        = "Integration-Pass-1!",
                    ["Input.ConfirmPassword"] = "Integration-Pass-1!",
                    ["__RequestVerificationToken"] = token,
                })
            };
            req.Headers.Add("X-Forwarded-For", forwardedFor);
            return (await client.SendAsync(req)).StatusCode;
        }

        // Spends the one permit belonging to the rightmost address.
        Assert.Equal(HttpStatusCode.Redirect, await RegisterFrom("203.0.113.1, 198.51.100.7"));

        // A different client-supplied value on the left does not buy a fresh bucket: still the same client.
        Assert.Equal(HttpStatusCode.TooManyRequests, await RegisterFrom("203.0.113.9, 198.51.100.7"));

        // Changing the entry the proxy appended is what moves the bucket — and only the proxy can do that.
        Assert.Equal(HttpStatusCode.Redirect, await RegisterFrom("203.0.113.1, 198.51.100.8"));

        // A single entry is the ordinary shape behind one proxy, and it is that address that counts.
        Assert.Equal(HttpStatusCode.Redirect, await RegisterFrom("198.51.100.9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RegisterFrom("198.51.100.9"));
    }

    [Fact]
    public async Task OAuth_start_sends_Google_an_https_redirect_uri_when_the_proxy_forwards_https()
    {
        var (parent, inner, oauth, _) = GmailTestHost.Boot();
        using var _ = parent; using var __ = inner;
        using var factory = BehindProxy(inner);
        var client = GmailTestHost.Client(factory);
        await Http.RegisterAsync(client);

        // Control: a direct http request still yields an http redirect_uri (local development).
        Assert.Equal("http://localhost/Integrations/Gmail/Callback", RedirectUriOf(await client.GetAsync("/Integrations/Gmail/Connect")));

        var res = await client.SendAsync(Proxied(HttpMethod.Get, "/Integrations/Gmail/Connect"));
        Assert.Equal($"https://{PublicHost}/Integrations/Gmail/Callback", RedirectUriOf(res));
        Assert.Equal($"https://{PublicHost}/Integrations/Gmail/Callback", oauth.LastRedirectUri);

        // The state cookie rides on an https redirect, so it must be marked Secure.
        var stateCookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(Controllers.IntegrationsController.StateCookie));
        Assert.Contains("secure", stateCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Google_RedirectBaseUrl_overrides_the_derived_redirect_uri_verbatim()
    {
        var (parent, inner, oauth, _) = GmailTestHost.Boot(true, ("Google:RedirectBaseUrl", "https://pinned.example/"));
        using var _ = parent; using var __ = inner;
        using var factory = BehindProxy(inner);
        var client = GmailTestHost.Client(factory);
        await Http.RegisterAsync(client);

        // No forwarded headers at all: the configured origin wins regardless of what the request looks like.
        Assert.Equal("https://pinned.example/Integrations/Gmail/Callback", RedirectUriOf(await client.GetAsync("/Integrations/Gmail/Connect")));
        Assert.Equal("https://pinned.example/Integrations/Gmail/Callback", oauth.LastRedirectUri);

        // And forwarded headers naming a different host do not change it.
        Assert.Equal("https://pinned.example/Integrations/Gmail/Callback", RedirectUriOf(await client.SendAsync(Proxied(HttpMethod.Get, "/Integrations/Gmail/Connect"))));
    }

    /// <summary>
    /// Boots a host whose reset emails are captured, optionally with <c>Email:BaseUrl</c> set.
    /// </summary>
    private static (WebApplicationFactory<Program> Factory, CapturingEmailSender Emails) ResetLinkHost(
        TestAppFactory parent, string? emailBaseUrl = null)
    {
        var emails = new CapturingEmailSender();
        var configured = emailBaseUrl is null
            ? parent
            : parent.WithWebHostBuilder(b => b.UseSetting(EmailLinkBase.ConfigKey, emailBaseUrl));

        return (BehindProxy(configured, s =>
        {
            s.RemoveAll<IAppEmailSender>();
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IAppEmailSender>(emails);
            s.AddSingleton<IEmailSender>(emails);
        }), emails);
    }

    /// <summary>Asks for a reset for <paramref name="email"/> over a request claiming <paramref name="host"/>.</summary>
    private static async Task RequestResetAsync(HttpClient client, string email, string host, bool forwarded)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/ForgotPassword");
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = token,
        });

        HttpRequestMessage req;
        if (forwarded)
        {
            req = ProxiedFrom(host, HttpMethod.Post, "/Identity/Account/ForgotPassword", content);
        }
        else
        {
            // No proxy in front: the caller's own Host header is all there is.
            req = new HttpRequestMessage(HttpMethod.Post, "/Identity/Account/ForgotPassword") { Content = content };
            req.Headers.Host = host;
        }

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);
    }

    /// <summary>
    /// The reset link is the one absolute URL the app puts in somebody's mailbox, so with
    /// <c>Email:BaseUrl</c> configured the host named in it must not be a host the caller chose — neither a
    /// spoofed <c>Host</c> header nor a spoofed <c>X-Forwarded-Host</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]   // X-Forwarded-Host, as a proxied request carries it
    [InlineData(false)]  // a plain Host header on a direct connection
    public async Task A_spoofed_host_cannot_change_the_emailed_reset_link(bool forwarded)
    {
        using var parent = new TestAppFactory();
        var (factory, emails) = ResetLinkHost(parent, "https://interntrackai.majdarow.com/");
        using var _ = factory;
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = await Http.RegisterAsync(client);

        await RequestResetAsync(client, email, "evil.example", forwarded);

        var (_, _, body) = Assert.Single(emails.Sent);
        Assert.Contains("https://interntrackai.majdarow.com/Identity/Account/ResetPassword?code=", body);
        Assert.DoesNotContain("evil.example", body);
        Assert.DoesNotContain("http://", body);
    }

    /// <summary>
    /// The control for the test above: with <c>Email:BaseUrl</c> unset the link still follows the request, so
    /// the assertions there are the setting doing the work and not something else in the pipeline. This is
    /// also the documented default, and what
    /// <see cref="Every_absolute_url_follows_the_host_the_request_arrived_on"/> depends on.
    /// </summary>
    [Fact]
    public async Task Without_a_configured_base_url_the_reset_link_still_follows_the_request()
    {
        using var parent = new TestAppFactory();
        var (factory, emails) = ResetLinkHost(parent);
        using var _ = factory;
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = await Http.RegisterAsync(client);

        await RequestResetAsync(client, email, "evil.example", forwarded: true);

        var (_, _, body) = Assert.Single(emails.Sent);
        Assert.Contains("https://evil.example/Identity/Account/ResetPassword?code=", body);
    }

    /// <summary>A base URL that isn't an absolute http(s) origin is ignored rather than pasted into a link.</summary>
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/Identity")]
    [InlineData("javascript:alert(1)")]
    [InlineData("   ")]
    public async Task An_unusable_base_url_falls_back_to_the_request(string configured)
    {
        using var parent = new TestAppFactory();
        var (factory, emails) = ResetLinkHost(parent, configured);
        using var _ = factory;
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = await Http.RegisterAsync(client);

        await RequestResetAsync(client, email, PublicHost, forwarded: true);

        var (_, _, body) = Assert.Single(emails.Sent);
        Assert.Contains($"https://{PublicHost}/Identity/Account/ResetPassword?code=", body);
    }

    [Fact]
    public async Task Password_reset_email_links_are_https_when_the_proxy_forwards_https()
    {
        var emails = new CapturingEmailSender();
        using var parent = new TestAppFactory();
        using var factory = BehindProxy(parent, s =>
        {
            // Both names have to be replaced: the page injects IAppEmailSender, Identity's own pages IEmailSender.
            s.RemoveAll<IAppEmailSender>();
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IAppEmailSender>(emails);
            s.AddSingleton<IEmailSender>(emails);
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = await Http.RegisterAsync(client);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/ForgotPassword");
        var res = await client.SendAsync(Proxied(HttpMethod.Post, "/Identity/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = token,
        })));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var (to, _, body) = Assert.Single(emails.Sent);
        Assert.Equal(email, to);
        Assert.Contains($"https://{PublicHost}/Identity/Account/ResetPassword?code=", body);
        Assert.DoesNotContain("http://", body);
    }

    [Fact]
    public async Task Bookmarklet_and_calendar_feed_urls_are_https_when_the_proxy_forwards_https()
    {
        using var parent = new TestAppFactory();
        using var factory = BehindProxy(parent);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Http.RegisterAsync(client);

        // Bookmarklet: the bookmark code targets the public https origin.
        var bookmarklet = await client.SendAsync(Proxied(HttpMethod.Get, "/Profile/Bookmarklet"));
        Assert.Equal(HttpStatusCode.OK, bookmarklet.StatusCode);
        var html = HttpUtility.HtmlDecode(await bookmarklet.Content.ReadAsStringAsync());
        Assert.Contains($"window.open('https://{PublicHost}/Capture?url='", html);
        Assert.DoesNotContain("http://localhost/Capture", html);

        // Calendar feed URL shown on the profile page.
        var profile = await client.SendAsync(Proxied(HttpMethod.Get, "/Profile"));
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var profileHtml = await profile.Content.ReadAsStringAsync();
        Assert.Matches($"https://{Regex.Escape(PublicHost)}/Calendar/feed\\.ics\\?token=", profileHtml);
        Assert.DoesNotContain("http://localhost/Calendar/feed.ics", profileHtml);
    }
}
