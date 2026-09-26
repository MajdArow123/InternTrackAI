using System.Net;
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

/// <summary>
/// The practice page's grouping, the progress card and the saved star — everything Phase 5 added to
/// <c>/Practice</c>.
/// </summary>
public class PracticeGroupingTests
{
    private sealed class Harness : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public ScriptedOpenAi Model { get; }

        public Harness(ScriptedOpenAi? model = null)
        {
            Model = model ?? new ScriptedOpenAi(ScriptedOpenAi.Questions(("A generated question?", "generated topic")));
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

        public async Task<int> SeedApplication(string userId, string company, string role)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var app = new JobApplication { UserId = userId, CompanyName = company, RoleTitle = role, JobDescription = "Ward work." };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();
            return app.Id;
        }

        public async Task<PracticeQuestion> SeedQuestion(
            string userId, string prompt, int? applicationId = null,
            PracticeDifficulty difficulty = PracticeDifficulty.Medium,
            string topic = "a topic", int? score = null, bool saved = false)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var q = new PracticeQuestion
            {
                UserId = userId, Prompt = prompt, Topic = topic, Difficulty = difficulty,
                Category = QuestionCategory.Technical, PromptHash = QuestionHash.Of(prompt),
                ApplicationId = applicationId, CreatedAt = DateTime.UtcNow,
                IsSaved = saved,
                Score = score,
                UserAnswer = score is null ? null : "An answer long enough to have been graded properly.",
                AnsweredAt = score is null ? null : DateTime.UtcNow
            };
            db.PracticeQuestions.Add(q);
            await db.SaveChangesAsync();
            return q;
        }

        public async Task<PracticeQuestion> Reload(int id)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .PracticeQuestions.AsNoTracking().FirstAsync(q => q.Id == id);
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private static async Task<string> Page(HttpClient client, string url = "/Practice")
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    // ── Grouping and the link back (§5.1) ────────────────────────────────────

    [Fact]
    public async Task Questions_are_grouped_under_the_application_they_were_generated_for()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var sunnybrook = await h.SeedApplication(userId, "Sunnybrook", "Student Nurse");
        var toronto    = await h.SeedApplication(userId, "Toronto General", "ICU Placement");

        await h.SeedQuestion(userId, "A Sunnybrook question?", sunnybrook);
        await h.SeedQuestion(userId, "A Toronto General question?", toronto);
        await h.SeedQuestion(userId, "A general practice question?");

        var html = await Page(client);

        Assert.Contains("Student Nurse", html);
        Assert.Contains("Sunnybrook", html);
        Assert.Contains("ICU Placement", html);
        Assert.Contains("Toronto General", html);
        Assert.Contains("General practice", html);

        // The deep link the applications page already honours.
        Assert.Contains($"/JobApplications#open-{sunnybrook}", html);
        Assert.Contains($"/JobApplications#open-{toronto}", html);

        // Each posting group offers its own generate button.
        Assert.Contains($"data-practice-generate-for=\"{sunnybrook}\"", html);
        Assert.Contains("Get more for this role", html);
    }

    [Fact]
    public async Task A_page_of_only_general_questions_shows_no_group_heading()
    {
        // "General practice" above the only list on the page labels nothing.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.SeedQuestion(userId, "A general practice question?");

        var html = await Page(client);

        Assert.Contains("A general practice question?", html);
        Assert.DoesNotContain("General practice", html);
        Assert.DoesNotContain("Get more for this role", html);
    }

    [Fact]
    public async Task Another_users_application_never_labels_a_group()
    {
        // The label lookup is owner-scoped, so a question pointing at someone else's posting renders
        // without leaking its company or role.
        using var h = new Harness();

        var bob = h.Client();
        var bobId = await h.UserIdOf(await Http.RegisterAsync(bob));
        var bobsApp = await h.SeedApplication(bobId, "Bob Secret Corp", "Bob Secret Role");

        var alice = h.Client();
        var aliceId = await h.UserIdOf(await Http.RegisterAsync(alice));
        await h.SeedQuestion(aliceId, "A question pointed at Bob's application?", bobsApp);

        var html = await Page(alice);

        Assert.Contains("A question pointed at Bob's application?", html);
        Assert.DoesNotContain("Bob Secret Corp", html);
        Assert.DoesNotContain("Bob Secret Role", html);
    }

    [Fact]
    public async Task Generating_for_a_role_stores_questions_against_that_application()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var appId = await h.SeedApplication(userId, "Sunnybrook", "Student Nurse");

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/GenerateMore")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["difficulty"] = "Hard",
                ["category"] = "Technical",
                ["applicationId"] = appId.ToString(),
                ["__RequestVerificationToken"] = token
            })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = h.Factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .PracticeQuestions.AsNoTracking().Where(q => q.UserId == userId).ToListAsync();

        Assert.NotEmpty(stored);
        Assert.All(stored, q => Assert.Equal(appId, q.ApplicationId));

        // The posting's description reached the prompt, which is what "for this role" has to mean.
        Assert.Contains("Ward work.", h.Model.Prompts[0]);
        Assert.Contains("Student Nurse", h.Model.Prompts[0]);
    }

    // ── The progress card (§5.3) ─────────────────────────────────────────────

    [Fact]
    public async Task The_progress_card_is_absent_until_there_is_something_to_show()
    {
        using var h = new Harness();
        var client = h.Client();
        await Http.RegisterAsync(client);

        Assert.DoesNotContain("Your progress", await Page(client));
    }

    [Fact]
    public async Task The_progress_card_reports_what_was_answered()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "Answered one?", topic: "triage", score: 2, difficulty: PracticeDifficulty.Easy);
        await h.SeedQuestion(userId, "Answered two?", topic: "triage", score: 2, difficulty: PracticeDifficulty.Easy);
        await h.SeedQuestion(userId, "Answered three?", topic: "triage", score: 2, difficulty: PracticeDifficulty.Easy);
        await h.SeedQuestion(userId, "Not answered?", topic: "handover");

        var html = await Page(client);

        Assert.Contains("Your progress", html);
        Assert.Contains("Answered", html);
        Assert.Contains("/4", html);                      // 3 of 4
        Assert.Contains("Weakest topic so far", html);
        Assert.Contains("triage", html);
    }

    [Fact]
    public async Task The_last_ten_average_is_hidden_until_there_are_more_than_ten_answers()
    {
        // Below the window it is the same number as the overall average, and two labels over one
        // number reads as a bug.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        for (var i = 0; i < 4; i++)
            await h.SeedQuestion(userId, $"Answered {i}?", topic: "triage", score: 3);

        Assert.DoesNotContain("Last 10 answers", await Page(client));

        for (var i = 4; i < 12; i++)
            await h.SeedQuestion(userId, $"Answered {i}?", topic: "triage", score: 3);

        Assert.Contains("Last 10 answers", await Page(client));
    }

    [Fact]
    public async Task No_trend_arrow_is_drawn_on_a_short_history()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        for (var i = 0; i < 4; i++)
            await h.SeedQuestion(userId, $"Answered {i}?", topic: "triage", score: 3);

        var html = await Page(client);

        Assert.Contains("Your progress", html);
        Assert.DoesNotContain("practice-trend", html);
    }

    [Fact]
    public async Task The_progress_card_ignores_the_filters()
    {
        // It is progress, not a summary of the current view — numbers that moved with a filter would
        // read as a bug.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "An easy one?", difficulty: PracticeDifficulty.Easy, score: 4);
        await h.SeedQuestion(userId, "A hard one?", difficulty: PracticeDifficulty.Hard, score: 4);

        var filtered = await Page(client, "/Practice?difficulty=Easy");

        Assert.DoesNotContain("A hard one?", filtered);   // the list really is filtered

        // Both difficulties still have their coverage row, and the Hard one still counts its question
        // even though the list below is showing only Easy.
        Assert.Contains("practice-coverage-easy", filtered);
        Assert.Contains("practice-coverage-hard", filtered);
        Assert.Equal(2, Occurrences(filtered, "<span class=\"funnel-count\">1/1</span>"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ── The denominator, pinned in both directions ───────────────────────────

    [Fact]
    public async Task The_denominator_grows_as_questions_are_added()
    {
        // Investigated after a report of 5/5 staying 5/5 across a second batch. The denominator was
        // never the bug — a batch that stores nothing (rate-limited, or deduped to zero) leaves it
        // unchanged, and both of those report themselves above the list. Pinned because it is exactly
        // the kind of thing that drifts.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        for (var i = 0; i < 5; i++)
            await h.SeedQuestion(userId, $"First batch {i}?", score: i == 0 ? 3 : null);

        // The Answered stat specifically — "/5" on its own also matches the score-out-of-5 beside it.
        const string answeredOf = "<span class=\"practice-progress-of\">";
        Assert.Contains($"1{answeredOf}/5</span>", await Page(client));

        for (var i = 0; i < 5; i++)
            await h.SeedQuestion(userId, $"Second batch {i}?");

        var after = await Page(client);
        Assert.Contains($"1{answeredOf}/10</span>", after);
        Assert.DoesNotContain($"1{answeredOf}/5</span>", after);
    }

    [Theory]
    [InlineData("/Practice?difficulty=Hard")]
    [InlineData("/Practice?category=Behavioral")]
    [InlineData("/Practice?saved=true")]
    [InlineData("/Practice?hideAnswered=true")]
    public async Task The_denominator_ignores_every_filter(string url)
    {
        // It is progress, not a summary of the current view. A denominator that moved with the filters
        // would make the card lie about how much is left.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "Easy technical unsaved unanswered?", difficulty: PracticeDifficulty.Easy);
        await h.SeedQuestion(userId, "Hard technical saved answered?", difficulty: PracticeDifficulty.Hard, score: 4, saved: true);
        await h.SeedQuestion(userId, "Medium technical unsaved answered?", score: 2);

        Assert.Contains("<span class=\"practice-progress-of\">/3</span>", await Page(client, url));
    }

    [Fact]
    public async Task Generating_more_questions_never_deletes_the_answered_ones()
    {
        // Answered questions are the history progress and saved questions are built on.
        using var h = new Harness(new ScriptedOpenAi(ScriptedOpenAi.Questions(("A brand new question?", "new topic"))));
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var answered = await h.SeedQuestion(userId, "An answered question?", score: 5, saved: true);

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/GenerateMore")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["difficulty"] = "Medium", ["category"] = "Technical", ["__RequestVerificationToken"] = token
            })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);

        var still = await h.Reload(answered.Id);
        Assert.Equal(5, still.Score);
        Assert.True(still.IsSaved);
        Assert.NotNull(still.AnsweredAt);
    }

    // ── Ordering (§4) ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_group_that_most_recently_got_questions_is_first()
    {
        // General practice used to sort last unconditionally, so questions generated from the page
        // button — which carry no ApplicationId — always rendered below every application group, i.e.
        // underneath everything already finished.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var appId = await h.SeedApplication(userId, "Sunnybrook", "Student Nurse");
        await h.SeedQuestion(userId, "An older application question?", appId);
        await h.SeedQuestion(userId, "A newer general question?");

        var html = await Page(client);

        Assert.True(html.IndexOf("A newer general question?", StringComparison.Ordinal)
                    < html.IndexOf("An older application question?", StringComparison.Ordinal),
            "the newest group should render first");
    }

    [Fact]
    public async Task Hide_answered_leaves_only_what_is_left_to_do()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "Already scored?", score: 4);
        await h.SeedQuestion(userId, "Still to do?");

        var html = await Page(client, "/Practice?hideAnswered=true");

        Assert.Contains("Still to do?", html);
        Assert.DoesNotContain("Already scored?", html);
    }

    [Fact]
    public async Task Hiding_answered_when_everything_is_answered_says_so()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        await h.SeedQuestion(userId, "The only question, already scored?", score: 4);

        var html = await Page(client, "/Practice?hideAnswered=true");

        Assert.Contains("You've answered everything that matches these filters.", html);
    }

    [Fact]
    public async Task The_manage_section_renders_inside_the_page_content_column()
    {
        // It shipped OUTSIDE .container-narrow, so on a wide screen it rendered full-bleed at x=0 while
        // every other element started at x=250 — present in the DOM, aligned with nothing, and
        // reported as "I can't find them anywhere". A width-independent assertion, because the bug was
        // invisible at the narrow viewport it was first checked at.
        using var h = new Harness();
        var client = h.Client();
        await h.SeedQuestion(await h.UserIdOf(await Http.RegisterAsync(client)), "A question?");

        var html = await Page(client);

        var containerAt = html.IndexOf("container-narrow", StringComparison.Ordinal);
        var manageAt = html.IndexOf("practice-manage", StringComparison.Ordinal);
        var containerClosesAt = html.LastIndexOf("</div>", StringComparison.Ordinal);

        Assert.True(containerAt >= 0 && manageAt > containerAt,
            "the manage section should render after the content container opens");
        Assert.True(manageAt < containerClosesAt,
            "the manage section should render inside the content container, not after it closes");
    }

    // ── Managing practice data ───────────────────────────────────────────────

    private static async Task<HttpResponseMessage> Post(HttpClient client, string action)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
        return await client.PostAsync($"/Practice/{action}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
    }

    [Fact]
    public async Task Clearing_unanswered_keeps_every_answer_score_and_star()
    {
        // The whole point of splitting the two buttons: this one must never cost anything the user did.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        var answered = await h.SeedQuestion(userId, "Answered, keep me?", score: 4);
        var savedUnanswered = await h.SeedQuestion(userId, "Starred but unanswered, keep me?", saved: true);
        var plain = await h.SeedQuestion(userId, "Plain unanswered, clear me?");

        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "ClearUnanswered")).StatusCode);

        Assert.Equal(4, (await h.Reload(answered.Id)).Score);
        Assert.True((await h.Reload(savedUnanswered.Id)).IsSaved);

        using var scope = h.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Null(await db.PracticeQuestions.FirstOrDefaultAsync(q => q.Id == plain.Id));
        Assert.Equal(2, await db.PracticeQuestions.CountAsync(q => q.UserId == userId));
    }

    [Fact]
    public async Task Deleting_everything_removes_answers_and_scores_too()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "Answered?", score: 5, saved: true);
        await h.SeedQuestion(userId, "Unanswered?");

        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "DeleteAll")).StatusCode);

        using var scope = h.Factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .PracticeQuestions.CountAsync(q => q.UserId == userId));
    }

    [Fact]
    public async Task Neither_reset_touches_another_users_questions()
    {
        using var h = new Harness();

        var bob = h.Client();
        var bobsQuestion = await h.SeedQuestion(await h.UserIdOf(await Http.RegisterAsync(bob)), "Bob's question?");

        var alice = h.Client();
        var aliceId = await h.UserIdOf(await Http.RegisterAsync(alice));
        await h.SeedQuestion(aliceId, "Alice's question?");

        await Post(alice, "ClearUnanswered");
        await Post(alice, "DeleteAll");

        Assert.NotNull(await h.Reload(bobsQuestion.Id));
    }

    [Fact]
    public async Task The_clear_button_counts_what_it_will_actually_remove()
    {
        // The count and the delete use the same predicate, so the button can't promise a number it
        // won't deliver.
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "Answered?", score: 3);
        await h.SeedQuestion(userId, "Saved?", saved: true);
        await h.SeedQuestion(userId, "Clearable one?");
        await h.SeedQuestion(userId, "Clearable two?");

        Assert.Contains("Clear 2 unanswered questions", await Page(client));
    }

    // ── Saved questions (§5.4) ───────────────────────────────────────────────

    private static async Task<HttpResponseMessage> Toggle(HttpClient client, int questionId)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Practice");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Practice/ToggleSaved")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["questionId"] = questionId.ToString(),
                ["__RequestVerificationToken"] = token
            })
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Starring_a_question_persists_and_unstarring_puts_it_back()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));
        var question = await h.SeedQuestion(userId, "Worth coming back to?");

        var first = await Toggle(client, question.Id);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"saved\":true", await first.Content.ReadAsStringAsync());
        Assert.True((await h.Reload(question.Id)).IsSaved);

        // The state survives a reload, rendered from the row rather than from the click.
        Assert.Contains("aria-pressed=\"true\"", await Page(client));

        var second = await Toggle(client, question.Id);
        Assert.Contains("\"saved\":false", await second.Content.ReadAsStringAsync());
        Assert.False((await h.Reload(question.Id)).IsSaved);
    }

    [Fact]
    public async Task The_saved_filter_shows_only_starred_questions()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "A starred question?", saved: true);
        await h.SeedQuestion(userId, "An ordinary question?");

        var html = await Page(client, "/Practice?saved=true");

        Assert.Contains("A starred question?", html);
        Assert.DoesNotContain("An ordinary question?", html);
    }

    [Fact]
    public async Task The_saved_filter_composes_with_difficulty()
    {
        using var h = new Harness();
        var client = h.Client();
        var userId = await h.UserIdOf(await Http.RegisterAsync(client));

        await h.SeedQuestion(userId, "Saved and hard?", difficulty: PracticeDifficulty.Hard, saved: true);
        await h.SeedQuestion(userId, "Saved but easy?", difficulty: PracticeDifficulty.Easy, saved: true);
        await h.SeedQuestion(userId, "Hard but unsaved?", difficulty: PracticeDifficulty.Hard);

        var html = await Page(client, "/Practice?saved=true&difficulty=Hard");

        Assert.Contains("Saved and hard?", html);
        Assert.DoesNotContain("Saved but easy?", html);
        Assert.DoesNotContain("Hard but unsaved?", html);
    }

    [Fact]
    public async Task Each_filter_group_marks_its_active_option_with_aria_current_and_stays_a_link()
    {
        using var h = new Harness();
        var client = h.Client();
        await Http.RegisterAsync(client);

        var html = await Page(client, "/Practice?difficulty=Hard&category=Behavioral&saved=true");

        // Every filter is a link with its own URL, and the state is announced rather than shown by colour
        // alone. aria-pressed is not allowed on a link (axe aria-allowed-attr), so it must not come back.
        var filters = System.Text.RegularExpressions.Regex
            .Matches(html, "<a class=\"practice-filter[^\"]*\"[^>]*>([^<]*)</a>")
            .Select(m => (Tag: m.Value, Text: m.Groups[1].Value.Trim()))
            .ToList();
        Assert.NotEmpty(filters);
        Assert.All(filters, f => Assert.Contains("href=\"/Practice", f.Tag));
        Assert.All(filters, f => Assert.DoesNotContain("aria-pressed", f.Tag));

        var current = filters.Where(f => f.Tag.Contains("aria-current=\"true\"")).Select(f => f.Text).ToList();
        Assert.Equal(new[] { "Hard", "Behavioral", "★ Saved" }, current);
    }

    [Fact]
    public async Task Another_users_question_cannot_be_starred()
    {
        using var h = new Harness();

        var bob = h.Client();
        var bobsQuestion = await h.SeedQuestion(await h.UserIdOf(await Http.RegisterAsync(bob)), "Bob's question?");

        var alice = h.Client();
        await Http.RegisterAsync(alice);

        Assert.Equal(HttpStatusCode.NotFound, (await Toggle(alice, bobsQuestion.Id)).StatusCode);
        Assert.False((await h.Reload(bobsQuestion.Id)).IsSaved);
    }
}
