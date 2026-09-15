using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
using Microsoft.Extensions.Logging;

namespace InternTrackAI.Tests.Integration;

/// <summary>Records every OpenAI request body and answers with <see cref="Reply"/>. Shared across the handlers the client factory builds.</summary>
public sealed class FakeOpenAi
{
    public List<string> Requests { get; } = new();
    public string Reply { get; set; } = Completion("{\"subject\":\"Following up on my application\",\"body\":\"Hello,\\n\\nChecking in.\\n\\nBest,\\nAlex\"}");

    public static string Completion(string content) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 900, completion_tokens = 120, total_tokens = 1020 }
    });

    /// <summary>The user message of request <paramref name="index"/>.</summary>
    public string UserPrompt(int index = 0) =>
        JsonDocument.Parse(Requests[index]).RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

    public string SystemPrompt(int index = 0) =>
        JsonDocument.Parse(Requests[index]).RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;

    public sealed class Handler(FakeOpenAi state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (state.Requests) state.Requests.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(state.Reply, Encoding.UTF8, "application/json") };
        }
    }
}

/// <summary>Captures every log line <see cref="FollowUpService"/> writes, formatted.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}

public class FollowUpEndpointTests
{
    private sealed class Host : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public FakeOpenAi OpenAi { get; } = new();
        public CapturingLogger<FollowUpService> Log { get; } = new();

        public Host(params (string Key, string Value)[] settings)
        {
            Factory = Parent.WithWebHostBuilder(b =>
            {
                b.UseSetting("OpenAI:ApiKey", "sk-test-not-real");
                foreach (var (k, v) in settings) b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    services.AddHttpClient<FollowUpService>().ConfigurePrimaryHttpMessageHandler(() => new FakeOpenAi.Handler(OpenAi));
                    // Resume upload runs profile auto-fill; keep that off the network too.
                    services.RemoveAll<IProfileExtractor>();
                    services.AddSingleton<IProfileExtractor>(new FakeProfileExtractor());
                    services.RemoveAll<ILogger<FollowUpService>>();
                    services.AddSingleton<ILogger<FollowUpService>>(Log);
                });
            });
        }

        public async Task<(HttpClient Client, string Token, string UserId)> UserAsync(string? email = null)
        {
            var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            email = await Http.RegisterAsync(client, email);
            var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            return (client, token, (await users.FindByEmailAsync(email))!.Id);
        }

        public async Task<T> WithDb<T>(Func<ApplicationDbContext, Task<T>> work)
        {
            using var scope = Factory.Services.CreateScope();
            return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        public Task<int> SeedAppAsync(string userId, Action<JobApplication>? tweak = null) => WithDb(async db =>
        {
            var app = new JobApplication
            {
                UserId = userId, CompanyName = "Stripe", RoleTitle = "Backend Intern", Status = ApplicationStatus.Applied,
                WorkMode = WorkMode.Hybrid, Location = "Toronto, ON", DateApplied = DateTime.UtcNow.Date.AddDays(-10),
                JobDescription = "Build payment services in Go."
            };
            tweak?.Invoke(app);
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        });

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static HttpRequestMessage Post(string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        req.Headers.Add("RequestVerificationToken", token);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    // ── Generate ──

    [Fact]
    public async Task Generate_assembles_stored_context_and_returns_subject_and_body()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();

        // Active resume (uploaded through the real endpoint), then profile, cover letter, notes, LastContactAt after applying.
        var upload = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var pdf = new ByteArrayContent(TestPdf.SampleResume());
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        upload.Add(pdf, "resume", "resume.pdf");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Profile/UploadResume", upload)).StatusCode);

        await h.WithDb(async db =>
        {
            var profile = await db.UserProfiles.FirstAsync(p => p.UserId == uid);
            profile.DisplayName = "Alex J";
            profile.SkillsJson = ProfileTags.ToJson(new[] { "Go", "PostgreSQL" });
            profile.PhoneNumber = "416-555-0100";
            await db.SaveChangesAsync();
            return 0;
        });
        var id = await h.SeedAppAsync(uid, a => { a.LastContactAt = DateTime.UtcNow.AddDays(-3); a.Deadline = DateTime.UtcNow.Date.AddDays(-5); });
        await h.WithDb(async db =>
        {
            db.GeneratedCoverLetters.Add(new GeneratedCoverLetter { UserId = uid, JobApplicationId = id, Content = "I have shipped Go services to production.", IsActive = true, VersionNumber = 1 });
            db.ApplicationNotes.Add(new ApplicationNote { UserId = uid, JobApplicationId = id, Text = "Recruiter Dana said to email her directly." });
            return await db.SaveChangesAsync();
        });

        var res = await client.SendAsync(Post($"/JobApplications/{id}/followup", token));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Json(res);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Following up on my application", json.GetProperty("subject").GetString());
        Assert.Equal("Hello,\n\nChecking in.\n\nBest,\nAlex", json.GetProperty("body").GetString());
        Assert.False(json.GetProperty("demo").GetBoolean());

        Assert.Single(h.OpenAi.Requests);
        var prompt = h.OpenAi.UserPrompt();
        Assert.Contains("Build payment services in Go.", prompt);
        Assert.Contains("Name for the sign-off: Alex J", prompt);
        Assert.Contains("Skills: Go, PostgreSQL", prompt);
        Assert.Contains("Backend intern at Example Corp", prompt);                 // resume text
        Assert.Contains("I have shipped Go services to production.", prompt);        // cover letter
        Assert.Contains("Recruiter Dana said to email her directly.", prompt);      // note
        Assert.Contains("SECOND FOLLOW-UP", prompt);                                  // LastContactAt after DateApplied
        Assert.Contains("(passed).", prompt);                                         // deadline carried from the application
        Assert.Contains("ASK: " + FollowUpService.AskInstruction(FollowUpAsk.AnythingFurther), prompt);
        Assert.DoesNotContain("416-555-0100", prompt);                                // profile phone never read
        Assert.Equal(FollowUpService.GenerateSystemPrompt, h.OpenAi.SystemPrompt());
        Assert.Contains("json_object", h.OpenAi.Requests[0]);

        // Nothing is stored.
        Assert.Equal(1, await h.WithDb(db => db.GeneratedCoverLetters.CountAsync(c => c.UserId == uid)));
        Assert.Equal(1, await h.WithDb(db => db.ApplicationNotes.CountAsync(n => n.UserId == uid)));
    }

    [Fact]
    public async Task Logs_name_the_application_and_token_count_but_nothing_from_the_email()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid, a => a.JobDescription = "SECRET-POSTING-TEXT");
        await h.WithDb(async db =>
        {
            db.ApplicationNotes.Add(new ApplicationNote { UserId = uid, JobApplicationId = id, Text = "SECRET-NOTE-TEXT" });
            return await db.SaveChangesAsync();
        });

        await client.SendAsync(Post($"/JobApplications/{id}/followup", token));
        await client.SendAsync(Post($"/JobApplications/{id}/followup/improve", token, new { subject = "SECRET-SUBJECT", body = "SECRET-DRAFT-BODY", instruction = "SECRET-INSTRUCTION" }));
        h.OpenAi.Reply = FakeOpenAi.Completion("not json");
        await client.SendAsync(Post($"/JobApplications/{id}/followup", token));

        var log = string.Join("\n", h.Log.Lines);
        Assert.Contains($"Follow-up draft generated for application {id} (1020 tokens).", log);
        Assert.Contains($"Follow-up draft improved for application {id} (1020 tokens).", log);
        Assert.Contains($"application {id} came back malformed", log);
        foreach (var secret in new[] { "SECRET", "Checking in", "Stripe", "Backend Intern", "Following up" })
            Assert.DoesNotContain(secret, log);
    }

    [Fact]
    public async Task Malformed_model_output_is_a_clean_json_error()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);

        foreach (var content in new[] { "Here is your email: Hello!", "{\"body\":\"no subject\"}" })
        {
            h.OpenAi.Reply = FakeOpenAi.Completion(content);
            var res = await client.SendAsync(Post($"/JobApplications/{id}/followup", token));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var json = await Json(res);
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal(FollowUpService.BadFormatError, json.GetProperty("error").GetString());
        }

        h.OpenAi.Reply = "{\"unexpected\":true}";                      // not even a completion envelope
        var broken = await Json(await client.SendAsync(Post($"/JobApplications/{id}/followup", token)));
        Assert.False(broken.GetProperty("success").GetBoolean());
        Assert.Equal(FollowUpService.BadFormatError, broken.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Only_applications_waiting_on_a_reply_can_be_drafted()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid, a => a.Status = ApplicationStatus.Interview);

        var json = await Json(await client.SendAsync(Post($"/JobApplications/{id}/followup", token)));
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(Controllers.FollowUpController.NotWaitingError, json.GetProperty("error").GetString());
        Assert.Empty(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Both_endpoints_require_the_antiforgery_token()
    {
        using var h = new Host();
        var (client, _, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);

        foreach (var url in new[] { $"/JobApplications/{id}/followup", $"/JobApplications/{id}/followup/improve" })
        {
            var res = await client.PostAsJsonAsync(url, new { subject = "s", body = "b", instruction = "shorter" });
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        }
        Assert.Empty(h.OpenAi.Requests);
    }

    // ── Improve ──

    [Fact]
    public async Task Improve_sends_the_current_draft_and_instruction_and_returns_the_revision()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);
        h.OpenAi.Reply = FakeOpenAi.Completion("{\"subject\":\"Backend Intern follow-up\",\"body\":\"Hello,\\n\\nShorter.\\n\\nBest,\"}");

        var res = await client.SendAsync(Post($"/JobApplications/{id}/followup/improve", token,
            new { subject = "Following up", body = "Hello,\n\nMy edited draft.\n\nBest,", instruction = "make it shorter" }));
        var json = await Json(res);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Backend Intern follow-up", json.GetProperty("subject").GetString());

        var prompt = h.OpenAi.UserPrompt();
        Assert.Contains("<draft>\nSubject: Following up\n\nHello,\n\nMy edited draft.\n\nBest,\n</draft>", prompt);
        Assert.Contains("<revision_request>\nmake it shorter\n</revision_request>", prompt);
        Assert.Equal(FollowUpService.ImproveSystemPrompt, h.OpenAi.SystemPrompt());
    }

    [Fact]
    public async Task Improve_validates_input_before_calling_the_model()
    {
        using var h = new Host();
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);

        foreach (var body in new object[]
        {
            new { subject = "s", body = "", instruction = "shorter" },
            new { subject = "s", body = "Hello", instruction = "  " },
            new { subject = "s", body = "Hello", instruction = new string('x', FollowUpService.MaxInstructionChars + 1) },
        })
        {
            var json = await Json(await client.SendAsync(Post($"/JobApplications/{id}/followup/improve", token, body)));
            Assert.False(json.GetProperty("success").GetBoolean());
        }
        Assert.Empty(h.OpenAi.Requests);
    }

    // ── Ownership ──

    [Fact]
    public async Task Another_users_application_is_404_on_both_endpoints_without_a_model_call()
    {
        using var h = new Host();
        var (_, _, bobId) = await h.UserAsync();
        var bobApp = await h.SeedAppAsync(bobId, a => a.CompanyName = "Bob Corp");
        var (alice, aliceToken, _) = await h.UserAsync();

        foreach (var url in new[] { $"/JobApplications/{bobApp}/followup", $"/JobApplications/{bobApp}/followup/improve" })
        {
            var res = await alice.SendAsync(Post(url, aliceToken, new { subject = "s", body = "b", instruction = "shorter" }));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.DoesNotContain("Bob", await res.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await alice.SendAsync(Post("/JobApplications/999999/followup", aliceToken))).StatusCode);
        Assert.Empty(h.OpenAi.Requests);
    }

    // ── Rate limit ──

    [Theory]
    [InlineData("followup")]
    [InlineData("followup/improve")]
    public async Task Rate_limit_is_enforced_with_the_standard_json_shape(string path)
    {
        using var h = new Host(("RateLimiting:AI:PermitLimit", "2"));
        var (client, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);
        var body = new { subject = "s", body = "Hello", instruction = "shorter" };

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post($"/JobApplications/{id}/{path}", token, body))).StatusCode);

        var rejected = await client.SendAsync(Post($"/JobApplications/{id}/{path}", token, body));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        var json = await Json(rejected);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("rateLimited").GetBoolean());
        Assert.Contains("limit of 2 AI requests", json.GetProperty("error").GetString());
        Assert.Equal(2, h.OpenAi.Requests.Count);
    }

    // ── Demo account ──

    [Fact]
    public async Task Demo_account_gets_the_canned_draft_and_no_model_call_and_cannot_improve()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Host(("Demo:Email", "  " + demoEmail.ToUpperInvariant() + " "), ("Demo:Password", "irrelevant-1!"));
        var (demo, token, uid) = await h.UserAsync(demoEmail);
        var id = await h.SeedAppAsync(uid, a => a.MatchingSkillsJson = "[\"Java\",\"SQL\"]");

        var res = await demo.SendAsync(Post($"/JobApplications/{id}/followup", token));
        var json = await Json(res);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("demo").GetBoolean());
        Assert.Equal("Following up on my Backend Intern application", json.GetProperty("subject").GetString());
        var body = json.GetProperty("body").GetString()!;
        Assert.Contains("Backend Intern position at Stripe", body);
        Assert.Contains("Java and SQL", body);

        var improve = await Json(await demo.SendAsync(Post($"/JobApplications/{id}/followup/improve", token, new { subject = "s", body = "Hello", instruction = "shorter" })));
        Assert.False(improve.GetProperty("success").GetBoolean());
        Assert.True(improve.GetProperty("demoRestricted").GetBoolean());
        Assert.Equal(ConfiguredAccounts.DemoUnavailableMessage, improve.GetProperty("error").GetString());

        Assert.Empty(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Demo_canned_responses_take_no_permit_from_the_demo_ai_bucket()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Host(("Demo:Email", demoEmail), ("Demo:Password", "irrelevant-1!"),
            ("RateLimiting:AI:PermitLimit", "50"), ("RateLimiting:AI:DemoPermitLimit", "1"));
        var (demo, token, uid) = await h.UserAsync(demoEmail);
        var id = await h.SeedAppAsync(uid);

        // Well past the demo's single permit: every canned draft and refused improve still answers normally.
        for (var i = 0; i < 4; i++)
        {
            var draft = await demo.SendAsync(Post($"/JobApplications/{id}/followup", token));
            Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
            Assert.True((await Json(draft)).GetProperty("demo").GetBoolean());

            var improve = await demo.SendAsync(Post($"/JobApplications/{id}/followup/improve", token, new { subject = "s", body = "Hello", instruction = "shorter" }));
            Assert.Equal(HttpStatusCode.OK, improve.StatusCode);
            Assert.True((await Json(improve)).GetProperty("demoRestricted").GetBoolean());
        }

        // The bucket is untouched: the one permit is still there for an endpoint that does call OpenAI, then it's gone.
        Assert.Equal(HttpStatusCode.BadRequest, (await demo.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await demo.PostAsJsonAsync("/Analyzer/Analyze", new { jobDescription = "" })).StatusCode);
        Assert.Empty(h.OpenAi.Requests);
    }

    [Fact]
    public async Task Permit_exemption_is_demo_only_regular_users_are_still_limited_after_demo_traffic()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        using var h = new Host(("Demo:Email", demoEmail), ("Demo:Password", "irrelevant-1!"), ("RateLimiting:AI:PermitLimit", "1"));
        var (demo, demoToken, demoId) = await h.UserAsync(demoEmail);
        var demoApp = await h.SeedAppAsync(demoId);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await demo.SendAsync(Post($"/JobApplications/{demoApp}/followup", demoToken))).StatusCode);

        var (user, token, uid) = await h.UserAsync();
        var id = await h.SeedAppAsync(uid);
        Assert.Equal(HttpStatusCode.OK, (await user.SendAsync(Post($"/JobApplications/{id}/followup", token))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await user.SendAsync(Post($"/JobApplications/{id}/followup", token))).StatusCode);
    }

    // ── Where the button renders ──

    [Fact]
    public async Task Draft_button_renders_on_follow_up_rows_and_the_drawer_only()
    {
        using var h = new Host();
        var (client, _, uid) = await h.UserAsync();
        var due  = await h.SeedAppAsync(uid, a => a.CompanyName = "Due Co");
        var soon = await h.SeedAppAsync(uid, a => { a.CompanyName = "Deadline Co"; a.Status = ApplicationStatus.Saved; a.DateApplied = null; a.Deadline = DateTime.UtcNow.Date.AddDays(2); });

        var dash = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.Contains($"data-followup-open data-app-id=\"{due}\"", dash);
        Assert.DoesNotContain($"data-followup-open data-app-id=\"{soon}\"", dash);
        Assert.Contains("id=\"followup-overlay\"", dash);
        Assert.Contains("/js/follow-up.js", dash);

        foreach (var url in new[] { "/JobApplications?view=list", "/JobApplications/Board" })
        {
            var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
            Assert.Contains("id=\"drawer-followup-btn\"", html);
            Assert.Contains("id=\"followup-overlay\"", html);
            Assert.Contains("/js/follow-up.js", html);
        }
        var board = await (await client.GetAsync("/JobApplications/Board")).Content.ReadAsStringAsync();
        Assert.Contains("data-follow-up-due=\"1\"", board);
    }
}
