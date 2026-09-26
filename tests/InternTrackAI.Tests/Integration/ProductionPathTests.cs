using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>Only added to the test host by <see cref="ProductionPathTests"/>: an action that throws, to reach the exception handler.</summary>
[Route("__test/throw")]
public class ThrowingTestController : Controller
{
    [HttpGet] public IActionResult Get() => throw new InvalidOperationException("secret-detail-7f3a: must never reach the page");
}

/// <summary>
/// The two request-pipeline branches Program.cs switches off in Development: the production exception
/// handler (<c>UseExceptionHandler("/Home/Error")</c>) and HSTS. Neither can be seen by anything run
/// against a Development server — which is every e2e dimension that uses :5240 — so they are pinned here,
/// in the <c>Testing</c> environment, which takes the non-Development branch (CLAUDE.md §12, environments).
/// </summary>
public class ProductionPathTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ProductionPathTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task An_unhandled_exception_renders_the_error_page_without_leaking_the_exception()
    {
        var client = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddControllersWithViews().AddApplicationPart(typeof(ThrowingTestController).Assembly)))
            .CreateClient();

        var res = await client.GetAsync("/__test/throw");
        var html = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Contains("Something went wrong", html);                 // our page, not the framework's
        Assert.DoesNotContain("secret-detail-7f3a", html);            // no exception text
        Assert.DoesNotContain("InvalidOperationException", html);     // no type names or stack
        Assert.True(res.Headers.Contains("Content-Security-Policy"), "the error page lost the security headers");
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task HSTS_is_sent_on_a_request_that_arrived_as_https_through_the_proxy()
    {
        // Production's shape: Railway terminates TLS and forwards plain HTTP with X-Forwarded-Proto, so HSTS
        // depends on UseForwardedHeaders having run first. Checked on the live site 2026-09-26: max-age=2592000.
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", "interntrackai.example");
        req.Headers.Add("X-Forwarded-For", "10.0.0.7");
        var res = await _factory.CreateClient().SendAsync(req);

        Assert.True(res.Headers.TryGetValues("Strict-Transport-Security", out var hsts), "no HSTS on an https request");
        Assert.Contains("max-age=", string.Join(",", hsts!));
    }

    [Fact]
    public async Task HSTS_is_not_sent_over_plain_http()
    {
        // The other half, so the test above cannot pass by HSTS being sent unconditionally.
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-Host", "interntrackai.example");
        var res = await _factory.CreateClient().SendAsync(req);

        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }
}
