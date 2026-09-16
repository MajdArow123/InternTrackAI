using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// <c>POST /JobApplications/KeywordCoverage</c>: owner-scoped by application id, or a bare posting for the
/// Create form. Deterministic, so it makes no AI call and takes no rate-limit permit — including on the demo
/// account, which uses it exactly like anyone else.
/// </summary>
public class KeywordCoverageEndpointTests
{
    private const string Posting = """
        Backend Engineering Intern

        Requirements:
        * Experience with Docker and Kubernetes
        * Familiarity with Terraform, Grafana, and Prometheus
        * Knowledge of Jenkins, Django, and Angular
        * Understanding of RabbitMQ and Cassandra
        * Exposure to Kafka and Redis
        """;

    private sealed class Host : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }

        public Host(params (string Key, string Value)[] settings)
        {
            Factory = Parent.WithWebHostBuilder(b =>
            {
                foreach (var (k, v) in settings) b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IProfileExtractor>();
                    services.AddSingleton<IProfileExtractor>(new FakeProfileExtractor());
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        /// <summary>A registered user with an uploaded (and therefore extracted) resume.</summary>
        public async Task<(HttpClient Client, string UserId)> UserAsync(string? email = null, bool withResume = true)
        {
            var client = Client();
            var registered = await Http.RegisterAsync(client, email);

            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var userId = (await users.FindByEmailAsync(registered))!.Id;

            if (withResume) await UploadAsync(client);
            return (client, userId);
        }

        public static async Task UploadAsync(HttpClient client)
        {
            var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
            var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
            var file = new ByteArrayContent(TestPdf.SampleResume());
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "resume", "resume.pdf");
            // The upload redirects back to the profile; the client does not follow redirects here.
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Profile/UploadResume", form)).StatusCode);
        }

        public async Task<int> SeedAppAsync(string userId, string? description = Posting)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication
            {
                UserId = userId,
                CompanyName = "Northwind",
                RoleTitle = "Backend Engineering Intern",
                JobDescription = description
            };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token,
                                                             int? appId = null, string? description = null)
    {
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        if (appId is not null) fields["AppId"] = appId.Value.ToString();
        if (description is not null) fields["Description"] = description;

        var req = new HttpRequestMessage(HttpMethod.Post, "/JobApplications/KeywordCoverage")
        {
            Content = new FormUrlEncodedContent(fields)
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static List<string> MissingTerms(JsonElement body) =>
        body.GetProperty("missing").EnumerateArray().Select(m => m.GetProperty("term").GetString()!).ToList();

    private static Task<string> TokenAsync(HttpClient client) =>
        Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");

    // ── Happy path ───────────────────────────────────────

    [Fact]
    public async Task An_application_returns_the_terms_its_posting_uses_and_the_resume_does_not()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId);

        var body = await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId));

        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.True(body.GetProperty("total").GetInt32() >= 10);

        var missing = MissingTerms(body);
        Assert.Contains("Kubernetes", missing);
        Assert.Contains("Terraform", missing);
        // The sample resume says Docker, Python, React, SQL, Git and PostgreSQL.
        Assert.DoesNotContain("Docker", missing);
        Assert.True(body.GetProperty("covered").GetInt32() > 0);
    }

    [Fact]
    public async Task Each_missing_term_carries_the_posting_line_it_came_from()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId);

        var body = await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId));
        var terraform = body.GetProperty("missing").EnumerateArray()
            .First(m => m.GetProperty("term").GetString() == "Terraform");

        Assert.Contains("Terraform", terraform.GetProperty("context").GetString());
    }

    [Fact]
    public async Task A_posting_with_no_id_is_checked_from_the_posted_text()
    {
        // The Create form's shape: there is no application yet, only what the user pasted.
        using var h = new Host();
        var (client, _) = await h.UserAsync();

        var body = await JsonOf(await PostAsync(client, await TokenAsync(client), description: Posting));

        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Contains("Kubernetes", MissingTerms(body));
    }

    [Fact]
    public async Task The_check_reflects_the_resume_that_is_active_now()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId);

        var before = MissingTerms(await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId)));
        Assert.Contains("Kubernetes", before);

        // Upload a second resume that does mention Kubernetes and make it active.
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(TestPdf.WithText(
            "Alex Johnson - Platform Engineer",
            "Skills: Kubernetes, Terraform, Grafana, Prometheus, Kafka",
            "Experience: Ran production clusters and owned the deployment pipeline."));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "resume", "platform.pdf");
        await client.PostAsync("/Profile/UploadResume", form);

        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var versions = await db.ResumeVersions.Where(r => r.UserId == userId).OrderBy(r => r.VersionNumber).ToListAsync();
            foreach (var v in versions) v.IsActive = v.Id == versions[^1].Id;
            await db.SaveChangesAsync();
        }

        var after = MissingTerms(await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId)));

        // Nothing was cached against the old resume, so the answer simply changed.
        Assert.DoesNotContain("Kubernetes", after);
        Assert.Contains("Django", after);
    }

    // ── Ownership ────────────────────────────────────────

    [Fact]
    public async Task Another_users_application_is_404_not_a_result()
    {
        using var h = new Host();
        var (alice, aliceId) = await h.UserAsync();
        var (bob, _) = await h.UserAsync();

        var aliceApp = await h.SeedAppAsync(aliceId);
        var res = await PostAsync(bob, await TokenAsync(bob), appId: aliceApp);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task An_application_that_does_not_exist_is_404()
    {
        using var h = new Host();
        var (client, _) = await h.UserAsync();

        var res = await PostAsync(client, await TokenAsync(client), appId: 987654);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_requires_authentication()
    {
        using var h = new Host();
        var anonymous = h.Client();

        var res = await PostAsync(anonymous, "", description: Posting);

        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_requires_an_antiforgery_token()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId);

        var res = await PostAsync(client, token: "not-a-real-token", appId: appId);

        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    // ── The unavailable states ───────────────────────────

    [Fact]
    public async Task An_application_with_no_posting_says_so_instead_of_erroring()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId, description: null);

        var body = await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId));

        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Equal(KeywordCoverageService.NoDescription, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_user_with_no_resume_says_so_instead_of_erroring()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync(withResume: false);
        var appId = await h.SeedAppAsync(userId);

        var body = await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId));

        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Equal(KeywordCoverageService.NoResume, body.GetProperty("reason").GetString());
        Assert.Empty(body.GetProperty("missing").EnumerateArray());
    }

    [Fact]
    public async Task A_posting_too_thin_to_check_says_so()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId, description: "Backend intern wanted. Apply within.");

        var body = await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId));

        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Equal(KeywordCoverageService.TooShort, body.GetProperty("reason").GetString());
    }

    // ── No AI, therefore no limit and no demo branch ─────

    [Fact]
    public async Task The_demo_account_uses_it_normally()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Host(("Demo:Email", demoEmail), ("Demo:Password", "irrelevant-here-1!"));
        var (client, userId) = await h.UserAsync(demoEmail);
        var appId = await h.SeedAppAsync(userId);

        var body = await JsonOf(await PostAsync(client, await TokenAsync(client), appId: appId));

        // No canned result, no refusal: there is no model call to protect the demo account from.
        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Contains("Kubernetes", MissingTerms(body));
    }

    [Fact]
    public async Task It_never_draws_from_the_shared_ai_rate_limit_bucket()
    {
        // One permit for the whole window: if this endpoint took one, the second call would 429.
        using var h = new Host(("RateLimiting:AI:PermitLimit", "1"), ("RateLimiting:AI:WindowMinutes", "60"));
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId);

        for (var i = 0; i < 5; i++)
        {
            var res = await PostAsync(client, await TokenAsync(client), appId: appId);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }

    // ── It renders where it is meant to ──────────────────

    [Fact]
    public async Task The_section_is_on_the_list_the_board_and_the_edit_page()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId);

        foreach (var url in new[] { "/JobApplications", "/JobApplications/Board", $"/JobApplications/Edit/{appId}" })
        {
            var html = WebUtility.HtmlDecode(await (await client.GetAsync(url)).Content.ReadAsStringAsync());
            Assert.Contains("data-keyword-coverage", html);
            Assert.Contains("Keyword coverage", html);
            Assert.Contains("keyword-coverage.js", html);
        }
    }

    [Fact]
    public async Task The_edit_page_points_its_section_at_that_application()
    {
        using var h = new Host();
        var (client, userId) = await h.UserAsync();
        var appId = await h.SeedAppAsync(userId);

        var html = await (await client.GetAsync($"/JobApplications/Edit/{appId}")).Content.ReadAsStringAsync();

        Assert.Contains($"data-app-id={appId}", html);
    }

    [Fact]
    public async Task The_helper_line_says_it_is_not_the_match_score()
    {
        using var h = new Host();
        var (client, _) = await h.UserAsync();

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/JobApplications/Create")).Content.ReadAsStringAsync());

        Assert.Contains("for resume scanners", html);
        Assert.Contains("Different from the match score", html);
    }
}
