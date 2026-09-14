using System.Net;
using System.Text.RegularExpressions;
using System.Web;
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
/// calendar feed, the public profile link) must therefore come out as https once those headers are
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
    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            Sent.Add((email, subject, htmlMessage));
            return Task.CompletedTask;
        }
    }

    /// <summary>A request as Railway's proxy would deliver it: plain http to the container, public origin in the headers.</summary>
    private static HttpRequestMessage Proxied(HttpMethod method, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", PublicHost);
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

    [Fact]
    public async Task Password_reset_email_links_are_https_when_the_proxy_forwards_https()
    {
        var emails = new CapturingEmailSender();
        using var parent = new TestAppFactory();
        using var factory = BehindProxy(parent, s =>
        {
            s.RemoveAll<IEmailSender>();
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
    public async Task Bookmarklet_calendar_feed_and_public_profile_urls_are_https_when_the_proxy_forwards_https()
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

        // Public profile link, both from the toggle endpoint's JSON and the re-rendered profile page.
        var antiforgery = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var toggle = await client.SendAsync(Proxied(HttpMethod.Post, "/Profile/TogglePublic", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["isPublic"] = "true",
            ["__RequestVerificationToken"] = antiforgery,
        })));
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);
        var json = await toggle.Content.ReadAsStringAsync();
        Assert.Contains($"\"url\":\"https://{PublicHost}/p/", json);

        var after = await (await client.SendAsync(Proxied(HttpMethod.Get, "/Profile"))).Content.ReadAsStringAsync();
        Assert.Contains($"https://{PublicHost}/p/", after);
        Assert.DoesNotContain("http://localhost/p/", after);
    }
}
