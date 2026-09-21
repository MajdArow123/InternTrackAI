using System.Net;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>The practice page and its "Get more" endpoint.</summary>
public class PracticePageTests
{
    private sealed class Harness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public ScriptedOpenAi Model { get; }

        public Harness(ScriptedOpenAi model)
        {
            Model = model;
            Factory = Parent.WithWebHostBuilder(b =>
            {
                b.UseSetting("OpenAI:ApiKey", "sk-test-not-a-real-key");
                b.ConfigureServices(services =>
                    services.AddHttpClient<PracticeQuestionService>().ConfigurePrimaryHttpMessageHandler(() => Model));
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<string> UserIdOf(string email)
        {
            using var scope = Factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
        }

        public async Task<int> SeedApplication(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = "Shopify", RoleTitle = "Backend Intern", JobDescription = "Build things." };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        }

        public async Task<List<PracticeQuestion>> QuestionsOf(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .PracticeQuestions.AsNoTracking().Where(q => q.UserId == userId).ToListAsync();
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static ScriptedOpenAi FiveQuestions() => new(ScriptedOpenAi.Questions(
        ("What happens when a connection pool is exhausted?", "connection pooling"),
        ("How would you size a pool for a bursty workload?",  "pool sizing"),
        ("When is a prepared statement cached per connection?", "prepared statements"),
        ("What breaks if two requests share one connection?", "connection lifetime"),
        ("How do you detect a leaked connection?",            "connection leaks")));

    private static async Task<JsonElement> GenerateMore(HttpClient client, string? difficulty = "Hard", string? category = "Technical", int? applicationId = null)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        if (difficulty is not null) form["difficulty"] = difficulty;
        if (category is not null) form["category"] = category;
        if (applicationId is { } id) form["applicationId"] = id.ToString();

        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/GenerateMore") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_page_renders_its_empty_state_and_the_dual_role_of_the_filters()
    {
        using var h = new Harness(FiveQuestions());
        var client = h.Client();
        await Http.RegisterAsync(client);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());

        Assert.Contains("No questions yet", html);
        // The copy has to say the filters also drive generation, or changing one reads as a bug.
        Assert.Contains("and", html);
        Assert.Contains("decide what", html);
        Assert.Contains("Get more questions", html);
    }

    [Fact]
    public async Task Get_more_returns_rendered_cards_the_client_appends()
    {
        // A partial, not JSON: the card markup lives in one place and is not duplicated client-side.
        using var h = new Harness(FiveQuestions());
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var body = await GenerateMore(client);

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(5, body.GetProperty("added").GetInt32());

        var html = WebUtility.HtmlDecode(body.GetProperty("html").GetString()!);
        Assert.Contains("practice-card", html);
        Assert.Contains("connection pooling", html);
        Assert.Contains("What happens when a connection pool is exhausted?", html);
        Assert.Contains("Hard", html);                       // the difficulty badge

        Assert.Equal(5, (await h.QuestionsOf(userId)).Count);
    }

    [Fact]
    public async Task Generated_questions_show_up_on_the_page_afterwards()
    {
        using var h = new Harness(FiveQuestions());
        var client = h.Client();
        await Http.RegisterAsync(client);
        await GenerateMore(client);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());

        Assert.Contains("How do you detect a leaked connection?", html);
        Assert.DoesNotContain("No questions yet", html);
    }

    [Fact]
    public async Task Filtering_narrows_the_list_and_says_so_when_it_empties_it()
    {
        using var h = new Harness(FiveQuestions());
        var client = h.Client();
        await Http.RegisterAsync(client);
        await GenerateMore(client, difficulty: "Hard");

        var easy = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice?difficulty=Easy")).Content.ReadAsStringAsync());

        Assert.Contains("Nothing at this level yet", easy);
        Assert.DoesNotContain("How do you detect a leaked connection?", easy);
    }

    [Fact]
    public async Task A_foreign_application_id_is_a_404_before_anything_is_generated()
    {
        using var h = new Harness(FiveQuestions());

        var owner = h.Client();
        var ownerId = await h.UserIdOf(await Http.RegisterAsync(owner));
        var appId = await h.SeedApplication(ownerId);

        var stranger = h.Client();
        var strangerId = await h.UserIdOf(await Http.RegisterAsync(stranger));

        var token = await Http.GetAntiforgeryTokenAsync(stranger, "/Practice");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/GenerateMore")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["applicationId"] = appId.ToString()
            })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(req)).StatusCode);
        Assert.Equal(0, h.Model.Calls);                       // refused before spending a call
        Assert.Empty(await h.QuestionsOf(strangerId));
    }

    [Fact]
    public async Task Questions_generated_for_an_application_also_appear_on_the_practice_page()
    {
        // One store: the practice page is not a second silo.
        using var h = new Harness(FiveQuestions());
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId);

        await GenerateMore(client, applicationId: appId);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/Practice")).Content.ReadAsStringAsync());
        Assert.Contains("What happens when a connection pool is exhausted?", html);
        Assert.All(await h.QuestionsOf(userId), q => Assert.Equal(appId, q.ApplicationId));
    }

    [Fact]
    public async Task An_exhausted_topic_returns_a_note_rather_than_an_error()
    {
        using var h = new Harness(FiveQuestions());
        var client = h.Client();
        await Http.RegisterAsync(client);

        await GenerateMore(client);
        var second = await GenerateMore(client);

        Assert.True(second.GetProperty("success").GetBoolean());
        Assert.Equal(0, second.GetProperty("added").GetInt32());
        Assert.Contains("covered a lot of ground", second.GetProperty("note").GetString());
    }
}
