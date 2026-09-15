using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InternTrackAI.Tests.Integration;

public class RateLimitTests
{
    private static (TestAppFactory Parent, WebApplicationFactory<Program> Factory) Boot(params (string Key, string Value)[] settings)
    {
        var parent = new TestAppFactory();
        var factory = parent.WithWebHostBuilder(b => { foreach (var (k, v) in settings) b.UseSetting(k, v); });
        return (parent, factory);
    }

    private static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // /Analyzer/Analyze validates the body before touching OpenAI, so an empty payload is a
    // cheap, network-free request that still counts against the limiter.
    private static Task<HttpResponseMessage> CheapAiCall(HttpClient c) =>
        c.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" });

    [Fact]
    public async Task Ai_endpoints_return_a_friendly_json_429_after_the_per_user_limit()
    {
        var (parent, factory) = Boot(("RateLimiting:AI:PermitLimit", "3"), ("RateLimiting:AI:WindowMinutes", "60"));
        using var _ = parent; using var __ = factory;

        var alice = Client(factory);
        await Http.RegisterAsync(alice);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(alice)).StatusCode);

        var rejected = await CheapAiCall(alice);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter is not null, "Retry-After header missing");
        Assert.Contains("application/json", rejected.Content.Headers.ContentType!.ToString());

        var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.True(body.GetProperty("hasResume").GetBoolean());
        Assert.Contains("limit of 3 AI requests per hour", body.GetProperty("error").GetString());

        // A different user has their own bucket.
        var bob = Client(factory);
        await Http.RegisterAsync(bob);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(bob)).StatusCode);
    }

    [Fact]
    public async Task Demo_account_gets_the_tighter_limit()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        var (parent, factory) = Boot(
            ("RateLimiting:AI:PermitLimit", "50"),
            ("RateLimiting:AI:DemoPermitLimit", "1"),
            ("Demo:Email", demoEmail),
            ("Demo:Password", "irrelevant-here-1!"));
        using var _ = parent; using var __ = factory;

        var demo = Client(factory);
        await Http.RegisterAsync(demo, demoEmail);

        Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(demo)).StatusCode);
        var rejected = await CheapAiCall(demo);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("limit of 1 AI requests", body.GetProperty("error").GetString());

        // Everyone else still has the normal allowance.
        var regular = Client(factory);
        await Http.RegisterAsync(regular);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(regular)).StatusCode);
    }

    [Fact]
    public async Task Plain_form_posts_are_redirected_back_with_an_error_toast()
    {
        var (parent, factory) = Boot(("RateLimiting:AI:PermitLimit", "1"));
        using var _ = parent; using var __ = factory;

        var client = Client(factory);
        await Http.RegisterAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(client)).StatusCode);   // uses the single permit

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Profile/ScoreResume")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token })
        };
        req.Headers.Referrer = new Uri("http://localhost/Profile");

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("http://localhost/Profile", res.Headers.Location!.ToString());

        var page = await client.GetAsync("/Profile");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("app-toast-error", html);
        Assert.Contains("reached the limit of 1 AI requests", html);
    }

    /// <summary>
    /// [NoAiCallForDemo] lets the demo account skip the AI bucket, so it may only sit on rate-limited actions whose demo
    /// branch never reaches OpenAI. The list is pinned: adding the attribute elsewhere must be a deliberate change here.
    /// </summary>
    [Fact]
    public void Demo_permit_exemption_is_only_on_the_reviewed_canned_actions()
    {
        var marked = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods().Where(m => m.IsDefined(typeof(InternTrackAI.Services.NoAiCallForDemoAttribute), false)))
            .ToList();

        Assert.Equal(new[] { "FollowUpController.Generate", "FollowUpController.Improve" },
            marked.Select(m => $"{m.DeclaringType!.Name}.{m.Name}").OrderBy(n => n));
        Assert.All(marked, m => Assert.True(m.IsDefined(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false),
            $"{m.Name} is exempt for the demo but not rate-limited at all"));
    }

    [Fact]
    public async Task Non_ai_endpoints_are_not_rate_limited()
    {
        var (parent, factory) = Boot(("RateLimiting:AI:PermitLimit", "1"));
        using var _ = parent; using var __ = factory;

        var client = Client(factory);
        await Http.RegisterAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CheapAiCall(client)).StatusCode);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/JobApplications")).StatusCode);
    }
}
