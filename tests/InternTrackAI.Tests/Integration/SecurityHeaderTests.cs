using System.Net;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The headers have to reach <i>every</i> response, not just the pretty ones: an MVC page, an
/// Identity page, the anonymous health endpoint, a static file, an authenticated page and the
/// re-executed 404 all go out through different parts of the pipeline, and each has its own way of
/// missing a middleware. The Identity pages get their own case because something in that stack
/// emits <c>X-Frame-Options: SAMEORIGIN</c> — it was the only such header in the app before this
/// middleware existed — and the weaker value must not be the one that ships.
/// </summary>
public class SecurityHeaderTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SecurityHeaderTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
        : res.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(", ", contentValues)
        : null;

    private static void AssertAllPresent(HttpResponseMessage res, string where)
    {
        Assert.Equal(SecurityHeaders.ContentTypeOptions, Header(res, SecurityHeaders.ContentTypeOptionsHeader));
        Assert.Equal(SecurityHeaders.FrameOptions,       Header(res, SecurityHeaders.FrameOptionsHeader));
        Assert.Equal(SecurityHeaders.ReferrerPolicy,     Header(res, SecurityHeaders.ReferrerPolicyHeader));
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header(res, SecurityHeaders.CspReportOnlyHeader));
        Assert.True(true, where);
    }

    [Theory]
    [InlineData("/")]                               // MVC page, anonymous
    [InlineData("/Home/Privacy")]                   // MVC page, second controller action
    [InlineData("/Identity/Account/Login")]         // Identity Razor Page (sets its own X-Frame-Options)
    [InlineData("/Identity/Account/Register")]
    [InlineData("/Identity/Account/ForgotPassword")]
    [InlineData("/health")]                         // endpoint, not a page, AllowAnonymous
    [InlineData("/css/site.css")]                   // static file: UseStaticFiles short-circuits
    [InlineData("/js/site.js")]
    public async Task Every_response_carries_the_security_headers(string url)
    {
        var res = await NewClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertAllPresent(res, url);
    }

    [Fact]
    public async Task X_Frame_Options_is_DENY_on_Identity_pages_too()
    {
        // SAMEORIGIN appears on these pages from somewhere in the Identity stack; DENY has to be
        // what actually goes out. Asserted on the outcome, so it holds whichever way the middleware
        // writes the header.
        var res = await NewClient().GetAsync("/Identity/Account/Login");

        Assert.Equal("DENY", Header(res, SecurityHeaders.FrameOptionsHeader));
        Assert.DoesNotContain("SAMEORIGIN", Header(res, SecurityHeaders.FrameOptionsHeader)!);
    }

    [Fact]
    public async Task An_authenticated_page_carries_the_headers()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);

        var res = await client.GetAsync("/Home/Dashboard");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertAllPresent(res, "/Home/Dashboard");
    }

    [Fact]
    public async Task The_404_page_carries_the_headers()
    {
        // 404s are re-executed through /Home/NotFound, which is a second trip down the pipeline.
        var res = await NewClient().GetAsync("/no/such/route");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        AssertAllPresent(res, "404");
    }

    [Fact]
    public async Task A_redirect_response_carries_the_headers()
    {
        // Anonymous request to an authorised page: a 302 never reaches MVC's result pipeline.
        var res = await NewClient().GetAsync("/JobApplications");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        AssertAllPresent(res, "auth redirect");
    }

    [Fact]
    public async Task The_CSP_is_report_only_so_the_remaining_inline_scripts_still_run()
    {
        var res = await NewClient().GetAsync("/");

        // Enforcing it would break _Layout's theme pre-paint and Prep.cshtml's inline block.
        Assert.Null(Header(res, "Content-Security-Policy"));
        Assert.NotNull(Header(res, SecurityHeaders.CspReportOnlyHeader));
    }

    [Fact]
    public void The_CSP_does_not_grant_unsafe_inline_to_scripts()
    {
        // The whole point of report-only here is to surface each inline block. 'unsafe-inline' on
        // script-src would silence the reports while changing nothing about the exposure.
        var scriptSrc = SecurityHeaders.ContentSecurityPolicy
            .Split(';', StringSplitOptions.TrimEntries)
            .Single(d => d.StartsWith("script-src", StringComparison.Ordinal));

        Assert.Equal("script-src 'self'", scriptSrc);
    }

    [Theory]
    // Every origin and scheme the app genuinely loads. If one of these is dropped the policy starts
    // reporting against working features, and whoever enforces it later will break them for real.
    [InlineData("style-src", "https://fonts.googleapis.com")]  // the layouts load Inter
    [InlineData("font-src", "https://fonts.gstatic.com")]      // ...and its font files
    [InlineData("img-src", "blob:")]                           // profile photo preview before upload
    [InlineData("img-src", "data:")]                           // inline SVG and the favicon set
    [InlineData("form-action", "'self'")]                      // Gmail OAuth leaves by 302, not a post
    public void The_CSP_allows_what_the_app_actually_loads(string directive, string expected)
    {
        var value = SecurityHeaders.ContentSecurityPolicy
            .Split(';', StringSplitOptions.TrimEntries)
            .Single(d => d.StartsWith(directive + " ", StringComparison.Ordinal));

        Assert.Contains(expected, value);
    }

    [Fact]
    public void Kestrel_does_not_advertise_itself()
    {
        // Asserted on the configured option rather than on a response, because the tests run on
        // TestServer, which is not Kestrel and never writes a Server header either way. The option
        // is what ships, so the option is what has to be pinned.
        var kestrel = _factory.Services
            .GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False(kestrel.AddServerHeader);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/css/site.css")]
    public async Task No_response_names_the_server(string url)
    {
        // Belt to the option's braces: nothing else in the pipeline (a middleware, a proxy shim)
        // may put the banner back.
        var res = await NewClient().GetAsync(url);

        Assert.Null(Header(res, "Server"));
    }

    [Fact]
    public async Task The_Gmail_OAuth_entry_point_is_not_broken_by_the_headers()
    {
        // Gmail is unconfigured in tests, so Connect 404s — the point is that adding the headers did
        // not turn it into an error, and that whatever it answers still carries them.
        var client = NewClient();
        await Http.RegisterAsync(client);

        var res = await client.GetAsync("/Integrations/Gmail/Connect");

        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Redirect,
            $"Gmail Connect returned {res.StatusCode}");
        AssertAllPresent(res, "gmail connect");
    }
}
