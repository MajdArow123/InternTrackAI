using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The guided tour is defined entirely in <c>wwwroot/js/tour-steps.js</c>, which no C# code reads at
/// runtime — so these tests are what keeps the definitions honest against the views. They parse that
/// file (its object is written as strict JSON for exactly this reason) and check every step against the
/// page it declares.
///
/// Targets are restricted to <c>#id</c> and <c>[data-tour="hook"]</c> so the check can be an exact string
/// match on the rendered HTML: a class or descendant selector would need an HTML parser, and adding one
/// is a new dependency. <see cref="Targets_are_ids_or_data_tour_hooks"/> fails if a looser selector
/// appears, rather than letting the coverage quietly lapse.
/// </summary>
public class TourTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public TourTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // ── The definitions file ──────────────────────────────────────────────

    /// <summary>One thing a step can spotlight. A single-target step is a list of one.</summary>
    private record Candidate(string? View, string? Target, string Title, string Body, string? When);
    private record Step(string? View, string Placement, Candidate[] Candidates);
    private record Tour(string Id, string[] Match, bool HasAutoKey, Step[] Steps);

    private static string StepsFilePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot", "js", "tour-steps.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("wwwroot/js/tour-steps.js not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The JSON object literal assigned to window.TourSteps, brace-matched out of the file.</summary>
    private static string StepsJson()
    {
        var source = File.ReadAllText(StepsFilePath());
        var assign = source.IndexOf("window.TourSteps", StringComparison.Ordinal);
        Assert.True(assign >= 0, "tour-steps.js must assign window.TourSteps");

        var start = source.IndexOf('{', assign);
        Assert.True(start >= 0, "window.TourSteps must be assigned an object literal");

        int depth = 0;
        bool inString = false, escaped = false;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }
        throw new Xunit.Sdk.XunitException("window.TourSteps object literal is not brace-balanced");
    }

    private static List<Tour> Tours()
    {
        using var doc = JsonDocument.Parse(StepsJson());
        var tours = new List<Tour>();
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        foreach (var tour in doc.RootElement.EnumerateObject())
        {
            var steps = tour.Value.GetProperty("steps").EnumerateArray().Select(s =>
            {
                var stepView = Str(s, "view");
                var entries  = s.TryGetProperty("targets", out var list) ? list.EnumerateArray().ToArray() : new[] { s };
                var candidates = entries.Select(c => new Candidate(
                    c.TryGetProperty("view", out _) ? Str(c, "view") : stepView,
                    Str(c, "target"),
                    Str(c, "title") ?? Str(s, "title") ?? "",
                    Str(c, "body") ?? Str(s, "body") ?? "",
                    Str(c, "when"))).ToArray();
                return new Step(stepView, Str(s, "placement") ?? "", candidates);
            }).ToArray();

            tours.Add(new Tour(
                tour.Name,
                tour.Value.GetProperty("match").EnumerateArray().Select(m => m.GetString()!).ToArray(),
                tour.Value.TryGetProperty("auto", out _),
                steps));
        }
        return tours;
    }

    /// <summary>Top-level property names as written, so a duplicate key can't be hidden by last-one-wins parsing.</summary>
    private static List<string> RawTourIds()
    {
        var json = StepsJson();
        var ids = new List<string>();
        int depth = 0;
        bool inString = false, escaped = false;
        var current = new System.Text.StringBuilder();
        var startedAtDepth1 = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                if (escaped) { escaped = false; current.Append(c); continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"')
                {
                    inString = false;
                    if (startedAtDepth1)
                    {
                        // A property name is the string that a ':' follows.
                        var rest = json.AsSpan(i + 1).TrimStart();
                        if (rest.Length > 0 && rest[0] == ':') ids.Add(current.ToString());
                    }
                    continue;
                }
                current.Append(c);
                continue;
            }
            if (c == '"') { inString = true; escaped = false; current.Clear(); startedAtDepth1 = depth == 1; }
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
        }
        return ids;
    }

    // ── Shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void Every_step_has_a_title_and_a_body()
    {
        var tours = Tours();
        Assert.NotEmpty(tours);

        foreach (var tour in tours)
        {
            Assert.NotEmpty(tour.Steps);
            for (var i = 0; i < tour.Steps.Length; i++)
            {
                Assert.NotEmpty(tour.Steps[i].Candidates);
                Assert.Contains(tour.Steps[i].Placement, new[] { "auto", "top", "bottom" });
                foreach (var (c, j) in tour.Steps[i].Candidates.Select((c, j) => (c, j)))
                {
                    var where = $"{tour.Id}[{i}].{j}";
                    Assert.False(string.IsNullOrWhiteSpace(c.Title), $"{where} has no title");
                    Assert.False(string.IsNullOrWhiteSpace(c.Body),  $"{where} has no body");
                }
            }
        }
    }

    [Fact]
    public void Tour_ids_are_unique()
    {
        var ids = RawTourIds();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids.OrderBy(x => x), Tours().Select(t => t.Id).OrderBy(x => x));
        Assert.Contains("overview", ids);
    }

    [Fact]
    public void Targets_are_ids_or_data_tour_hooks()
    {
        // Anything looser (a class, a descendant chain) can't be verified against the rendered page
        // without an HTML parser, so it isn't allowed in the definitions.
        var allowed = new Regex("^(#[A-Za-z][-A-Za-z0-9_]*|\\[data-tour=\"[-A-Za-z0-9_]+\"\\])$");

        foreach (var tour in Tours())
            foreach (var c in tour.Steps.SelectMany(s => s.Candidates).Where(c => c.Target is not null))
                Assert.True(allowed.IsMatch(c.Target!),
                    $"{tour.Id}: target \"{c.Target}\" must be #id or [data-tour=\"hook\"]");
    }

    [Fact]
    public void Nothing_auto_runs_and_page_tours_claim_their_pages()
    {
        // The overview used to auto-run on the first dashboard visit, dimming the page a recruiter had just
        // arrived on. It is an invitation now; an "auto" key coming back would mean the engine grew the
        // behaviour back, so any tour declaring one fails.
        var tours = Tours();
        Assert.All(tours, t => Assert.False(t.HasAutoKey, $"{t.Id} declares \"auto\"; nothing auto-runs"));

        var overview = tours.Single(t => t.Id == "overview");
        Assert.Empty(overview.Match);                                   // the fallback claims no page
        Assert.All(tours.Where(t => t.Id != "overview"), t => Assert.NotEmpty(t.Match));

        // No two tours claim the same page, or the nav button's choice would be arbitrary.
        var claimed = tours.SelectMany(t => t.Match.Select(m => m.ToLowerInvariant())).ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
    }

    [Fact]
    public void The_overview_is_five_steps_at_most()
    {
        // The recruiter-facing tour. Longer, and it stops being the first two minutes.
        Assert.InRange(Tours().Single(t => t.Id == "overview").Steps.Length, 1, 5);
    }

    // ── Against the real pages ────────────────────────────────────────────

    /// <summary>
    /// The page a candidate is checked on: its own view, else the closest earlier step's, else the tour's
    /// pages.
    /// </summary>
    private static string[] PagesFor(Tour tour, int index, Candidate candidate)
    {
        if (candidate.View is string own) return new[] { own };
        for (var i = index; i >= 0; i--)
            if (tour.Steps[i].View is string view) return new[] { view };
        return tour.Match;
    }

    [Fact]
    public async Task Every_declared_view_is_a_real_route()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);

        // A candidate gated on a context flag ("when") is only tried when the flag says its page has
        // something on it — the review screen redirects without a draft — so it is checked, with its
        // flag's state seeded, by Every_target_resolves_on_the_page_it_declares instead.
        var paths = Tours()
            .SelectMany(t => t.Steps.Select(s => s.View)
                .Concat(t.Steps.SelectMany(s => s.Candidates).Where(c => c.When is null).Select(c => c.View))
                .Concat(t.Match))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var res = await client.GetAsync(path);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)res.StatusCode}");
        }
    }

    [Fact]
    public async Task Every_target_resolves_on_the_page_it_declares()
    {
        // Fallback targets are alternatives, so no single account shows all of them: the answered card
        // needs a practice history and the example card needs none. Each candidate must resolve in at
        // least one of two real states — a fully used account and a brand-new one.
        var full = NewClient();
        await SeedConditionalTargets(await UserIdOf(await Http.RegisterAsync(full)));
        var empty = NewClient();
        await Http.RegisterAsync(empty);

        var pages = new Dictionary<(HttpClient, string), string>();
        async Task<string> Page(HttpClient client, string path)
        {
            if (!pages.TryGetValue((client, path), out var html))
            {
                var res = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                pages[(client, path)] = html = await res.Content.ReadAsStringAsync();
            }
            return html;
        }

        foreach (var tour in Tours())
        {
            for (var i = 0; i < tour.Steps.Length; i++)
            {
                foreach (var (c, j) in tour.Steps[i].Candidates.Select((c, j) => (c, j)))
                {
                    if (c.Target is null) continue;

                    var needle = c.Target.StartsWith('#')
                        ? $"id=\"{c.Target[1..]}\""
                        : c.Target[1..^1];                 // [data-tour="x"] → data-tour="x"

                    // A flag-gated candidate is only tried when its flag is set, which the full account has
                    // (a pending draft); the empty one would redirect off the page.
                    var states = c.When is null ? new[] { full, empty } : new[] { full };

                    foreach (var path in PagesFor(tour, i, c))
                    {
                        var found = false;
                        foreach (var state in states)
                            if ((await Page(state, path)).Contains(needle, StringComparison.Ordinal)) { found = true; break; }
                        Assert.True(found, $"{tour.Id}[{i}].{j}: \"{c.Target}\" does not resolve on {path} for a full or an empty account");
                    }
                }
            }
        }
    }

    [Fact]
    public async Task Every_context_flag_a_step_waits_for_is_one_the_dashboard_provides()
    {
        // A "when" naming a flag the dashboard never writes would make that candidate unreachable forever,
        // silently. The flags are read from the dashboard's #tourContextData island.
        var client = NewClient();
        await Http.RegisterAsync(client);
        var html = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();

        var island = Regex.Match(html, "<script type=\"application/json\" id=\"tourContextData\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(island.Success, "the dashboard must render #tourContextData");
        using var ctx = JsonDocument.Parse(island.Groups[1].Value);

        foreach (var when in Tours().SelectMany(t => t.Steps).SelectMany(s => s.Candidates).Select(c => c.When).OfType<string>().Distinct())
            Assert.True(ctx.RootElement.TryGetProperty(when, out _), $"\"{when}\" is not in the dashboard's tour context");

        Assert.False(ctx.RootElement.GetProperty("pendingDraft").GetBoolean());   // a new account has nothing waiting
    }

    [Fact]
    public async Task The_invitation_is_one_welcome_not_two()
    {
        // A new account sees the onboarding card, so the invitation lives inside it and the header pill
        // is not rendered. Once there is an application the onboarding card goes and the pill takes over,
        // rendered hidden so tour.js can show it only to browsers that haven't answered it yet.
        var client = NewClient();
        var userId = await UserIdOf(await Http.RegisterAsync(client));

        var fresh = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"onboardingBanner\"", fresh);
        Assert.Contains("class=\"onboarding-tour-link\" data-tour-start=\"overview\"", fresh);
        Assert.DoesNotContain("data-tour-prompt", fresh);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.JobApplications.Add(new JobApplication { UserId = userId, CompanyName = "Tour Co", RoleTitle = "Intern" });
            await db.SaveChangesAsync();
        }

        var used = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"onboardingBanner\"", used);
        Assert.Matches("<span class=\"tour-prompt\" data-tour-prompt hidden>", used);
        Assert.Contains("data-tour-start=\"overview\"", used);
    }

    /// <summary>
    /// The "full" account: every target that only renders when there is something to show. An overdue
    /// Saved application and an interview inside the window (Attention card, its practice button), a
    /// pending inbox suggestion, an answered and an unanswered practice question, an active resume, and
    /// a pending resume draft (the review screen).
    /// </summary>
    private async Task SeedConditionalTargets(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var resume = new ResumeVersion
        {
            UserId = userId, VersionNumber = 1, OriginalFileName = "resume.pdf",
            StoredPath = $"resumes/{userId}/1.pdf", FileSize = 1, IsActive = true, Label = "General",
            ExtractedText = "Built things."
        };
        db.ResumeVersions.Add(resume);
        var interview = new JobApplication
        {
            UserId = userId, CompanyName = "Interview Co", RoleTitle = "Intern",
            Status = ApplicationStatus.Interview,
            InterviewAt = TestClock.Instant(TestClock.Today.AddDays(3), 14)   // inside the 14-day window
        };
        db.JobApplications.AddRange(
            new JobApplication
            {
                UserId = userId, CompanyName = "Tour Co", RoleTitle = "Intern",
                Status = ApplicationStatus.Saved,
                Deadline = TestClock.Today.AddDays(-3)      // overdue → the Attention card renders
            },
            interview);
        await db.SaveChangesAsync();

        db.StatusSuggestions.Add(new StatusSuggestion
        {
            UserId = userId, ApplicationId = interview.Id, GmailMessageId = "tour-test", SuggestedStatus = ApplicationStatus.Offer,
            Confidence = 0.9, Summary = "An offer.", EmailSubject = "Offer", EmailFrom = "a@b.c",
            EmailDate = DateTime.UtcNow, Status = SuggestionState.Pending, CreatedAt = DateTime.UtcNow
        });
        db.PracticeQuestions.AddRange(
            InternTrackAI.Services.DemoSeeder.BuildAnsweredPracticeQuestion(userId, DateTime.UtcNow),
            new PracticeQuestion
            {
                UserId = userId, Prompt = "An unanswered question?", Topic = "a topic",
                PromptHash = InternTrackAI.Services.QuestionHash.Of("An unanswered question?"), CreatedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<InternTrackAI.Services.ResumeParseService>()
            .StoreDemoParseAsync(userId, resume.Id, 100);
    }

    private async Task<string> UserIdOf(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.Id;
    }

    [Fact]
    public async Task Nav_tour_button_renders_only_on_pages_with_a_tour()
    {
        var anon = NewClient();

        // Anonymous: the landing page and the Identity pages (_AuthLayout) have no tour.
        foreach (var path in new[] { "/", "/Identity/Account/Login", "/Identity/Account/Register" })
        {
            var html = await (await anon.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("id=\"tour-btn\"", html);
            Assert.DoesNotContain("js/tour.js", html);
            Assert.DoesNotContain("css/tour.css", html);
        }

        // Signed in: every page resolves to a tour — a page tour, or the overview.
        var client = NewClient();
        await Http.RegisterAsync(client);

        foreach (var path in new[] { "/Home/Dashboard", "/JobApplications", "/JobApplications/Board", "/Profile", "/CoverLetter/Generate" })
        {
            var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.Contains("id=\"tour-btn\"", html);
            Assert.Contains("aria-label=\"Start guided tour\"", html);
            Assert.Contains("js/tour-steps.js", html);
            Assert.Contains("js/tour.js", html);
            Assert.Contains("css/tour.css", html);
        }
    }
}
