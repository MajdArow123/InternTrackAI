using System.Net;
using System.Web;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Drives <c>GET /Capture</c> — the bookmarklet's landing endpoint — end to end with the real
/// auth, rate-limit, and MVC pipeline. The OpenAI-backed analyzer is swapped for a scripted stub
/// so no network is touched and both the success and fallback paths are deterministic.
/// </summary>
public class CaptureTests
{
    private const string PostingUrl = "https://example.com/jobs/123?ref=board";
    private const string PageTitle  = "Software Engineering Intern - Example Co | Jobs";

    /// <summary>Replaces <see cref="JobAnalyzerService"/> with one whose <c>AnalyzeAsync</c> runs the given script.</summary>
    private sealed class StubAnalyzer : JobAnalyzerService
    {
        private readonly Func<string, CancellationToken, Task<JobAnalysisResult>> _script;
        public int Calls;

        /// <summary>The field context the controller built for the last call, so a test can assert it arrived.</summary>
        public string? LastProfileContext;

        public StubAnalyzer(IServiceProvider sp, Func<string, CancellationToken, Task<JobAnalysisResult>> script)
            : base(new HttpClient(),
                   sp.GetRequiredService<IHttpClientFactory>(),
                   sp.GetRequiredService<IConfiguration>(),
                   sp.GetRequiredService<ILogger<JobAnalyzerService>>())
        {
            _script = script;
        }

        public override Task<JobAnalysisResult> AnalyzeAsync(string input, string? profileContext = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            LastProfileContext = profileContext;
            return _script(input, cancellationToken);
        }
    }

    private static (TestAppFactory Parent, WebApplicationFactory<Program> Factory) Boot(
        Func<string, CancellationToken, Task<JobAnalysisResult>>? analyzer = null,
        params (string Key, string Value)[] settings)
    {
        var parent = new TestAppFactory();
        var factory = parent.WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            if (analyzer is not null)
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<JobAnalyzerService>();
                    services.AddTransient<JobAnalyzerService>(sp => new StubAnalyzer(sp, analyzer));
                });
        });
        return (parent, factory);
    }

    private static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Location header as path+query (Identity's login challenge emits an absolute URL, MVC redirects a relative one).</summary>
    private static string Location(HttpResponseMessage res)
    {
        var loc = res.Headers.Location ?? throw new Xunit.Sdk.XunitException("No Location header");
        return loc.IsAbsoluteUri ? loc.PathAndQuery : loc.ToString();
    }

    private static string CaptureUrl(string url = PostingUrl, string? title = PageTitle) =>
        "/Capture?url=" + Uri.EscapeDataString(url) + (title is null ? "" : "&title=" + Uri.EscapeDataString(title));

    private static Task<JobAnalysisResult> Extracted(string _, CancellationToken __) =>
        Task.FromResult(new JobAnalysisResult
        {
            Success = true, CompanyName = "Example Co", RoleTitle = "Software Engineering Intern",
            Location = "Remote (US)", Salary = "$45/hr", Skills = ["C#", "SQL"],
            Deadline = "2030-01-15", InterviewDate = "not a date"
        });

    private static Task<JobAnalysisResult> Failed(string _, CancellationToken __) =>
        Task.FromResult(new JobAnalysisResult { Success = false, Error = "Could not fetch the job posting." });

    [Fact]
    public async Task Valid_url_redirects_to_Create_with_the_extracted_fields_prefilled()
    {
        var (parent, factory) = Boot(Extracted);
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var res = await client.GetAsync(CaptureUrl());

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith("/JobApplications/Create?", location);

        var q = HttpUtility.ParseQueryString(new Uri("http://x" + location).Query);
        Assert.Equal(PostingUrl,                    q["url"]);
        Assert.Equal(PageTitle,                     q["title"]);
        Assert.Equal("Example Co",                  q["company"]);
        Assert.Equal("Software Engineering Intern", q["role"]);
        Assert.Equal("Remote (US)",                 q["location"]);
        Assert.Equal("$45/hr",                      q["salary"]);
        Assert.Equal("Remote",                      q["workMode"]);
        Assert.Equal("2030-01-15",                  q["deadline"]);

        // The Create GET binds those parameters and renders them into the form (nothing is saved).
        var page = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("value=\"Example Co\"", html);
        Assert.Contains("value=\"Software Engineering Intern\"", html);
        Assert.Contains("value=\"Remote (US)\"", html);
        Assert.Contains("value=\"$45/hr\"", html);
        Assert.Contains("value=\"2030-01-15\"", html);
        Assert.Contains("value=\"" + HttpUtility.HtmlEncode(PostingUrl) + "\"", html);
        Assert.Contains("<option selected=\"selected\" value=\"0\">Remote</option>", html);
        Assert.Contains("Saved from example.com", html);
        Assert.DoesNotContain("Couldn't read the posting automatically", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/jobs")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Unsafe_or_malformed_urls_return_400(string url)
    {
        var (parent, factory) = Boot(Extracted);
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var res = await client.GetAsync(CaptureUrl(url));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Urls_over_2048_characters_return_400()
    {
        var (parent, factory) = Boot(Extracted);
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var longUrl = "https://example.com/" + new string('a', 2048);
        var res = await client.GetAsync(CaptureUrl(longUrl));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_redirects_to_login_with_the_full_return_url()
    {
        var (parent, factory) = Boot(Extracted);
        using var _ = parent; using var __ = factory;
        var anon = Client(factory);

        var res = await anon.GetAsync(CaptureUrl());

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = Location(res);
        Assert.StartsWith("/Identity/Account/Login?", location);

        var returnUrl = HttpUtility.ParseQueryString(new Uri("http://x" + location).Query)["ReturnUrl"];
        Assert.Equal(CaptureUrl(), returnUrl);

        // The login page carries that ReturnUrl in its hidden field so the post-login redirect lands back on /Capture.
        var loginPage = await anon.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var html = await loginPage.Content.ReadAsStringAsync();
        Assert.Contains("name=\"ReturnUrl\" value=\"" + HttpUtility.HtmlAttributeEncode(CaptureUrl()) + "\"", html);
    }

    [Fact]
    public async Task Signing_in_from_the_login_redirect_lands_back_on_Capture_then_Create()
    {
        var (parent, factory) = Boot(Extracted);
        using var _ = parent; using var __ = factory;

        // Register (to have an account), then start a fresh anonymous client for the bookmarklet path.
        var register = Client(factory);
        var email = await Http.RegisterAsync(register);

        var anon = Client(factory);
        var first = await anon.GetAsync(CaptureUrl());
        var loginUrl = Location(first);
        var returnUrl = HttpUtility.ParseQueryString(new Uri("http://x" + loginUrl).Query)["ReturnUrl"]!;

        var token = await Http.GetAntiforgeryTokenAsync(anon, loginUrl);
        var login = await anon.PostAsync(loginUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]    = email,
            ["Input.Password"] = "Integration-Pass-1!",
            ["Input.RememberMe"] = "false",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(returnUrl, Location(login));

        var capture = await anon.GetAsync(Location(login));
        Assert.Equal(HttpStatusCode.Redirect, capture.StatusCode);
        var q = HttpUtility.ParseQueryString(new Uri("http://x" + capture.Headers.Location!).Query);
        Assert.Equal("Example Co", q["company"]);
        Assert.Equal(PostingUrl, q["url"]);
    }

    [Fact]
    public async Task Analyzer_failure_still_redirects_with_url_and_title_and_shows_the_info_toast()
    {
        var (parent, factory) = Boot(Failed);
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var res = await client.GetAsync(CaptureUrl());

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        var q = HttpUtility.ParseQueryString(new Uri("http://x" + location).Query);
        Assert.Equal(PostingUrl, q["url"]);
        Assert.Equal(PageTitle,  q["title"]);
        Assert.Null(q["company"]);
        Assert.Null(q["salary"]);

        var page = await client.GetAsync(location);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Couldn&#x27;t read the posting automatically", html);
        Assert.Contains("value=\"" + HttpUtility.HtmlEncode(PostingUrl) + "\"", html);
        // With no extracted role, the tab title stands in so the user has something to edit.
        Assert.Contains("value=\"" + HttpUtility.HtmlAttributeEncode(PageTitle) + "\"", html);
        Assert.Contains("Saved from example.com", html);
    }

    [Fact]
    public async Task No_api_key_configured_falls_back_without_calling_the_network()
    {
        // The real analyzer short-circuits when no OpenAI key is configured (the CI/test default).
        var (parent, factory) = Boot(analyzer: null);
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var res = await client.GetAsync(CaptureUrl());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var q = HttpUtility.ParseQueryString(new Uri("http://x" + res.Headers.Location!).Query);
        Assert.Equal(PostingUrl, q["url"]);
        Assert.Null(q["company"]);
    }

    [Fact]
    public async Task Analyzer_that_hangs_is_cut_off_at_the_timeout_and_falls_back()
    {
        var (parent, factory) = Boot(
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new JobAnalysisResult(); },
            ("Capture:AnalyzeTimeoutSeconds", "1"));
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await client.GetAsync(CaptureUrl());
        sw.Stop();

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
        var q = HttpUtility.ParseQueryString(new Uri("http://x" + res.Headers.Location!).Query);
        Assert.Equal(PostingUrl, q["url"]);
        Assert.Null(q["company"]);
    }

    [Fact]
    public async Task Capture_counts_against_the_ai_rate_limit()
    {
        var (parent, factory) = Boot(Extracted, ("RateLimiting:AI:PermitLimit", "1"), ("RateLimiting:AI:WindowMinutes", "60"));
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var first = await client.GetAsync(CaptureUrl());
        Assert.StartsWith("/JobApplications/Create?", first.Headers.Location!.ToString());

        // Plain-navigation callers are bounced with an error toast rather than shown a bare 429.
        var second = await client.GetAsync(CaptureUrl());
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.DoesNotContain("/JobApplications/Create", second.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Create_ignores_prefill_values_that_exceed_column_limits_instead_of_erroring()
    {
        var (parent, factory) = Boot(Extracted);
        using var _ = parent; using var __ = factory;
        var client = Client(factory);
        await Http.RegisterAsync(client);

        var hugeTitle = new string('T', 500);
        var page = await client.GetAsync("/JobApplications/Create?url=" + Uri.EscapeDataString(PostingUrl) + "&title=" + hugeTitle);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("value=\"" + new string('T', 100) + "\"", html);
        Assert.DoesNotContain(new string('T', 101), html);
    }
}
