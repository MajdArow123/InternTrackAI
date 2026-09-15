using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InternTrackAI.Tests.Integration;

/// <summary>POST /Profile/RewriteBullet: validation, ownership, rate limiting, the demo branch, and the Resume card's application selector.</summary>
public class ResumeRewriteEndpointTests
{
    private const string Url = "/Profile/RewriteBullet";

    private static readonly string ThreeVariants = FakeOpenAi.Completion(JsonSerializer.Serialize(new
    {
        variants = new[]
        {
            new { text = "Split the order monolith into [N] ASP.NET Core microservices", angle = "Impact first" },
            new { text = "Decomposed an order monolith into ASP.NET Core services", angle = "Technical detail" },
            new { text = "Built order microservices in ASP.NET Core", angle = "Concise" },
        }
    }));

    private sealed class Host : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public FakeOpenAi OpenAi { get; } = new() { Reply = ThreeVariants };

        public Host(params (string Key, string Value)[] settings)
        {
            Factory = Parent.WithWebHostBuilder(b =>
            {
                b.UseSetting("OpenAI:ApiKey", "sk-test-not-real");
                foreach (var (k, v) in settings) b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.AddHttpClient<ResumeRewriteService>().ConfigurePrimaryHttpMessageHandler(() => new FakeOpenAi.Handler(OpenAi));
                    services.RemoveAll<IProfileExtractor>();
                    services.AddSingleton<IProfileExtractor>(new FakeProfileExtractor());
                });
            });
        }

        public async Task<(HttpClient Client, string Token, string UserId)> UserAsync(string? email = null)
        {
            var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            email = await Http.RegisterAsync(client, email);
            var token = await Http.GetAntiforgeryTokenAsync(client, "/Profile");
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            return (client, token, (await users.FindByEmailAsync(email))!.Id);
        }

        public async Task<T> WithDb<T>(Func<ApplicationDbContext, Task<T>> work)
        {
            using var scope = Factory.Services.CreateScope();
            return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        public Task<int> SeedAppAsync(string userId, string company = "Shopify", string role = "Backend Intern", string? description = "Build microservices for checkout.") => WithDb(async db =>
        {
            var app = new JobApplication
            {
                UserId = userId, CompanyName = company, RoleTitle = role, Status = ApplicationStatus.Saved,
                WorkMode = WorkMode.Remote, JobDescription = description
            };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        });

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static HttpRequestMessage Post(string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(body) };
        req.Headers.Add("RequestVerificationToken", token);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Rewrites_a_bullet_for_the_chosen_application_and_stores_nothing()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        await h.WithDb(async db =>
        {
            var profile = await db.UserProfiles.FirstAsync(p => p.UserId == uid);   // created when /Profile rendered
            profile.SkillsJson = ProfileTags.ToJson(new[] { "C#", "Docker" });
            return await db.SaveChangesAsync();
        });
        var id = await h.SeedAppAsync(uid);
        var appsBefore = await h.WithDb(db => db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == id));

        var res = await client.SendAsync(Post(token, new { bullet = "• Worked on splitting the order monolith\ninto services", applicationId = id }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Json(res);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.False(json.GetProperty("demo").GetBoolean());
        var variants = json.GetProperty("variants").EnumerateArray().ToList();
        Assert.Equal(3, variants.Count);
        Assert.Equal(new[] { "Impact first", "Technical detail", "Concise" }, variants.Select(v => v.GetProperty("angle").GetString()));
        Assert.Equal("Split the order monolith into [N] ASP.NET Core microservices", variants[0].GetProperty("text").GetString());

        var request = Assert.Single(h.OpenAi.Requests);
        Assert.Contains("json_object", request);
        Assert.Equal(ResumeRewriteService.SystemPrompt, h.OpenAi.SystemPrompt());
        var prompt = h.OpenAi.UserPrompt();
        Assert.Contains("<job_description>\nBuild microservices for checkout.\n</job_description>", prompt);
        Assert.Contains("<bullet>\nWorked on splitting the order monolith into services\n</bullet>", prompt);
        Assert.Contains("Company: Shopify", prompt);
        // The profile's skills are stored but never sent: they made the model claim skills the bullet didn't have.
        Assert.DoesNotContain("applicant_skills", prompt);
        Assert.DoesNotContain("Docker", prompt);

        var appAfter = await h.WithDb(db => db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal(JsonSerializer.Serialize(appsBefore), JsonSerializer.Serialize(appAfter));
        Assert.Equal(0, await h.WithDb(db => db.ApplicationNotes.CountAsync()));
    }

    [Fact]
    public async Task Empty_and_over_long_bullets_are_refused_without_a_call()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);

        foreach (var bullet in new[] { "", "   \n • ", null })
        {
            var json = await Json(await client.SendAsync(Post(token, new { bullet, applicationId = id })));
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal("bullet", json.GetProperty("field").GetString());
            Assert.Equal("Paste a bullet to rewrite.", json.GetProperty("error").GetString());
        }

        var tooLong = await Json(await client.SendAsync(Post(token, new { bullet = "Built " + new string('x', 395), applicationId = id })));
        Assert.False(tooLong.GetProperty("success").GetBoolean());
        Assert.Contains("under 400 characters", tooLong.GetProperty("error").GetString());

        var exactly400 = await Json(await client.SendAsync(Post(token, new { bullet = "Built " + new string('x', 394), applicationId = id })));
        Assert.True(exactly400.GetProperty("success").GetBoolean());
        Assert.Single(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Application_without_a_job_description_explains_and_links_to_edit()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid, description: "   ");

        var res = await client.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = id }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Json(res);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("needsDescription").GetBoolean());
        Assert.Equal($"/JobApplications/Edit/{id}", json.GetProperty("editUrl").GetString());
        Assert.Equal(ResumeRewriteService.NoJobDescriptionError, json.GetProperty("error").GetString());
        Assert.Empty(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Another_users_application_is_a_404_and_no_call()
    {
        using var h = new Host();
        var (_, _, ownerId) = await h.UserAsync();
        var foreignId = await h.SeedAppAsync(ownerId);
        var (intruder, token, _) = await h.UserAsync();

        var res = await intruder.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = foreignId }));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False((await Json(res)).GetProperty("success").GetBoolean());

        var missing = await intruder.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = 999999 }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Requires_the_antiforgery_token()
    {
        using var h = new Host();
        var (client, _, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);

        var res = await client.PostAsJsonAsync(Url, new { bullet = "Built a thing", applicationId = id });
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Model_output_in_the_wrong_shape_is_a_clean_error()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);
        h.OpenAi.Reply = FakeOpenAi.Completion("{\"text\":\"Built a thing\",\"angle\":\"Concise\"}");

        var json = await Json(await client.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = id })));
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(ResumeRewriteService.BadFormatError, json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Is_rate_limited_under_the_shared_ai_policy()
    {
        using var h = new Host(("RateLimiting:AI:PermitLimit", "1"));
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = id }))).StatusCode);
        var rejected = await client.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = id }));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var json = await Json(rejected);
        Assert.True(json.GetProperty("rateLimited").GetBoolean());
        Assert.Contains("limit of 1 AI requests", json.GetProperty("error").GetString());
        Assert.Single(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Demo_account_gets_sample_rewrites_with_no_call_and_no_permit()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Host(("Demo:Email", " " + demoEmail.ToUpperInvariant() + "  "), ("Demo:Password", "irrelevant-1!"),
            ("RateLimiting:AI:PermitLimit", "50"), ("RateLimiting:AI:DemoPermitLimit", "1"));
        var (demo, token, uid) = await h.UserAsync(demoEmail);
        var id = await h.SeedAppAsync(uid);

        for (var i = 0; i < 4; i++)
        {
            var res = await demo.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = id }));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var json = await Json(res);
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("demo").GetBoolean());
            Assert.Equal(ResumeRewriteService.DemoSampleBullet, json.GetProperty("sampleBullet").GetString());
            Assert.Equal(ResumeRewriteService.DemoVariants().Select(v => v.Text), json.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("text").GetString()));
        }
        Assert.Empty(h.OpenAi.Requests);

        // The demo's single permit is untouched: one AI call that does reach the limiter succeeds, the next is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await demo.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await demo.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" })).StatusCode);

        // Ownership and validation still apply to the demo account.
        var (_, _, otherId) = await h.UserAsync();
        var foreign = await h.SeedAppAsync(otherId);
        Assert.Equal(HttpStatusCode.NotFound, (await demo.SendAsync(Post(token, new { bullet = "Built a thing", applicationId = foreign }))).StatusCode);
    }

    [Fact]
    public async Task Resume_card_lists_only_applications_with_a_description_newest_first()
    {
        using var h = new Host();
        var (client, _, uid) = await h.UserAsync();

        var empty = WebUtility.HtmlDecode(await client.GetStringAsync("/Profile"));
        Assert.Contains("Rewrite a bullet", empty);
        Assert.Contains("None of your applications has a job description yet.", empty);
        Assert.DoesNotContain("id=\"rewriteApp\"", empty);

        var older = await h.SeedAppAsync(uid, "Atlassian", "Platform Intern");
        await h.SeedAppAsync(uid, "NoDesc Co", "Data Intern", description: null);
        var newer = await h.SeedAppAsync(uid, "Wealthsimple", "API Intern");

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Profile"));
        Assert.DoesNotContain("None of your applications has a job description yet.", html);
        Assert.Contains("id=\"rewriteApp\"", html);
        Assert.DoesNotContain("NoDesc Co", html);
        var newerAt = html.IndexOf($"<option value=\"{newer}\">Wealthsimple — API Intern</option>", StringComparison.Ordinal);
        var olderAt = html.IndexOf($"<option value=\"{older}\">Atlassian — Platform Intern</option>", StringComparison.Ordinal);
        Assert.True(newerAt >= 0 && olderAt > newerAt, "options missing or not newest first");
        Assert.Contains("bullet-rewriter.js", html);
    }
}
