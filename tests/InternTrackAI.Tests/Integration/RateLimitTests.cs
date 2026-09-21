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
    /// A practice call that reaches the limiter without reaching OpenAI: the length rule rejects it
    /// first, but the policy has already taken its permit by then.
    /// </summary>
    private static async Task<HttpResponseMessage> CheapPracticeCall(HttpClient c)
    {
        var token = await Http.GetAntiforgeryTokenAsync(c, "/Practice");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/SubmitAnswer")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["questionId"] = "0", ["answer"] = "too short", ["__RequestVerificationToken"] = token
            })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await c.SendAsync(req);
    }

    /// <summary>
    /// <b>The reason the split exists.</b> Practice is many small calls and resume parsing is rare and
    /// expensive; sharing one bucket meant a demo visitor could not finish a single round of five
    /// questions without locking the rest of the app.
    /// </summary>
    [Fact]
    public async Task Exhausting_the_practice_bucket_leaves_every_other_ai_path_working()
    {
        var (parent, factory) = Boot(
            ("RateLimiting:AI:Practice:PermitLimit", "2"),
            ("RateLimiting:AI:Practice:WindowMinutes", "60"),
            ("RateLimiting:AI:PermitLimit", "5"),
            ("RateLimiting:AI:WindowMinutes", "60"));
        using var _ = parent; using var __ = factory;

        var client = Client(factory);
        await Http.RegisterAsync(client);

        // Spend the practice allowance.
        await CheapPracticeCall(client);
        await CheapPracticeCall(client);
        var practiceRejected = await CheapPracticeCall(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, practiceRejected.StatusCode);

        // The other bucket is untouched — this is the whole point.
        Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(client)).StatusCode);
    }

    [Fact]
    public async Task Exhausting_the_default_bucket_leaves_practice_working()
    {
        var (parent, factory) = Boot(
            ("RateLimiting:AI:PermitLimit", "1"),
            ("RateLimiting:AI:WindowMinutes", "60"),
            ("RateLimiting:AI:Practice:PermitLimit", "5"),
            ("RateLimiting:AI:Practice:WindowMinutes", "60"));
        using var _ = parent; using var __ = factory;

        var client = Client(factory);
        await Http.RegisterAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, (await CheapAiCall(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CheapAiCall(client)).StatusCode);

        // Practice still has its own allowance. 404 = it got past the limiter to the ownership check.
        var practice = await CheapPracticeCall(client);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, practice.StatusCode);
    }

    [Fact]
    public async Task A_practice_rejection_says_which_limit_was_hit_and_what_still_works()
    {
        // The complaint: "You've reached the limit of 10 AI requests per hour" read as the whole app
        // being locked, when resume parsing and cover letters were fine.
        var (parent, factory) = Boot(
            ("RateLimiting:AI:Practice:PermitLimit", "1"),
            ("RateLimiting:AI:Practice:WindowMinutes", "60"));
        using var _ = parent; using var __ = factory;

        var client = Client(factory);
        await Http.RegisterAsync(client);

        await CheapPracticeCall(client);
        var rejected = await CheapPracticeCall(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();

        Assert.Contains("practice limit", body);
        Assert.Contains("unaffected", body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("rateLimited").GetBoolean());
    }

    [Fact]
    public void The_daily_practice_window_reads_as_a_day_not_as_hours()
    {
        // 1440 minutes formatted by the generic branch would say "24 hours", which invites the reader
        // to think it resets at midnight. It does not — see AiUsageLimiter's remarks.
        var message = InternTrackAI.Services.AiRateLimiting.BuildMessage(
            100, 1440, null, InternTrackAI.Services.AiBucket.Practice);

        Assert.Equal("You've reached the practice limit of 100 AI requests per day. "
                     + "Resume tools, cover letters and job analysis are unaffected.", message);
    }

    /// <summary>
    /// The set of actions behind the AI bucket, pinned. Every one of them spends the maintainer's OpenAI
    /// key, so an attribute quietly disappearing in a refactor is the failure worth catching — nothing
    /// else in the suite would notice, because the endpoint keeps working perfectly.
    /// </summary>
    /// <remarks>
    /// <b>This pins what is marked, not everything that spends a call</b> — reflection can see the
    /// attribute, not which actions reach OpenAI. The gap is deliberate and covered elsewhere:
    /// <c>ProfileController.UploadResume</c> is **not** policied, because the policy's non-XHR
    /// rejection redirects the post away and that would cost the user their <em>upload</em>. It takes a
    /// permit from the same <see cref="InternTrackAI.Services.AiUsageLimiter"/> bucket by hand instead,
    /// so an empty bucket costs the parse and never the file
    /// (<c>ResumeUploadTests.A_rate_limited_upload_still_saves_the_file_and_draws_on_the_shared_ai_bucket</c>).
    /// Adding a row here therefore is not the same as "this endpoint is now limited" — check for a
    /// manual <c>TryAcquire</c> before concluding an absent action is unlimited.
    /// </remarks>
    [Fact]
    public void The_actions_behind_the_ai_policy_are_the_reviewed_set()
    {
        var limited = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false)
                         .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()
                         .Any(a => a.PolicyName == InternTrackAI.Services.AiRateLimiting.PolicyName))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[]
        {
            "AnalyzerController.Analyze",
            "CaptureController.Index",
            "CoverLetterController.GenerateAjax",
            "CoverLetterController.ImproveAjax",
            "FollowUpController.Generate",
            "FollowUpController.Improve",
            "InterviewPrepController.CritiqueAnswer",
            "InterviewPrepController.Generate",
            "ProfileController.AutoMatch",
            "ProfileController.ReparseResume",
            "ProfileController.RewriteBullet",
            "ProfileController.ScoreResume",
            "SalaryInsightController.Estimate",
        }, limited);
    }

    /// <summary>
    /// The practice bucket's actions, pinned separately. These two spend a different allowance from
    /// everything above, which is the entire point of the split — an action drifting between the two
    /// policies would silently change which budget it eats.
    /// </summary>
    [Fact]
    public void The_practice_policy_covers_exactly_generation_and_scoring()
    {
        var practice = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false)
                         .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()
                         .Any(a => a.PolicyName == InternTrackAI.Services.AiRateLimiting.PracticePolicyName))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[]
        {
            "PracticeController.GenerateMore",
            "PracticeController.SubmitAnswer",
        }, practice);
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

        Assert.Equal(new[] { "FollowUpController.Generate", "FollowUpController.Improve", "ProfileController.RewriteBullet" },
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
