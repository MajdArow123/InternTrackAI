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

    private record Step(string? View, string? Target, string Title, string Body, string Placement);
    private record Tour(string Id, string[] Match, string? Auto, Step[] Steps);

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
        foreach (var tour in doc.RootElement.EnumerateObject())
        {
            var steps = tour.Value.GetProperty("steps").EnumerateArray().Select(s => new Step(
                s.GetProperty("view").ValueKind   == JsonValueKind.Null ? null : s.GetProperty("view").GetString(),
                s.GetProperty("target").ValueKind == JsonValueKind.Null ? null : s.GetProperty("target").GetString(),
                s.GetProperty("title").GetString() ?? "",
                s.GetProperty("body").GetString() ?? "",
                s.GetProperty("placement").GetString() ?? "")).ToArray();

            tours.Add(new Tour(
                tour.Name,
                tour.Value.GetProperty("match").EnumerateArray().Select(m => m.GetString()!).ToArray(),
                tour.Value.GetProperty("auto").ValueKind == JsonValueKind.Null ? null : tour.Value.GetProperty("auto").GetString(),
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
                var where = $"{tour.Id}[{i}]";
                Assert.False(string.IsNullOrWhiteSpace(tour.Steps[i].Title), $"{where} has no title");
                Assert.False(string.IsNullOrWhiteSpace(tour.Steps[i].Body),  $"{where} has no body");
                Assert.Contains(tour.Steps[i].Placement, new[] { "auto", "top", "bottom" });
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
            foreach (var step in tour.Steps.Where(s => s.Target is not null))
                Assert.True(allowed.IsMatch(step.Target!),
                    $"{tour.Id}: target \"{step.Target}\" must be #id or [data-tour=\"hook\"]");
    }

    [Fact]
    public void Overview_auto_runs_and_page_tours_do_not()
    {
        var tours = Tours();
        var overview = tours.Single(t => t.Id == "overview");

        Assert.Equal("/Home/Dashboard", overview.Auto);
        Assert.Empty(overview.Match);                                   // the fallback claims no page
        Assert.All(tours.Where(t => t.Id != "overview"), t =>
        {
            Assert.Null(t.Auto);                                        // page tours are opt-in only
            Assert.NotEmpty(t.Match);
        });

        // No two tours claim the same page, or the nav button's choice would be arbitrary.
        var claimed = tours.SelectMany(t => t.Match.Select(m => m.ToLowerInvariant())).ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
    }

    // ── Against the real pages ────────────────────────────────────────────

    /// <summary>The page a step is checked on: its own view, else the closest earlier one, else the tour's pages.</summary>
    private static string[] PagesFor(Tour tour, int index)
    {
        for (var i = index; i >= 0; i--)
            if (tour.Steps[i].View is string view) return new[] { view };
        return tour.Match;
    }

    [Fact]
    public async Task Every_declared_view_is_a_real_route()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);

        var paths = Tours()
            .SelectMany(t => t.Steps.Select(s => s.View).Append(t.Auto).Concat(t.Match))
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
        var client = NewClient();
        var email  = await Http.RegisterAsync(client);
        await SeedConditionalTargets(await UserIdOf(email));

        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<string> Page(string path)
        {
            if (!pages.TryGetValue(path, out var html))
            {
                var res = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                pages[path] = html = await res.Content.ReadAsStringAsync();
            }
            return html;
        }

        foreach (var tour in Tours())
        {
            for (var i = 0; i < tour.Steps.Length; i++)
            {
                var target = tour.Steps[i].Target;
                if (target is null) continue;

                var needle = target.StartsWith('#')
                    ? $"id=\"{target[1..]}\""
                    : target[1..^1];                 // [data-tour="x"] → data-tour="x"

                foreach (var path in PagesFor(tour, i))
                    Assert.True((await Page(path)).Contains(needle, StringComparison.Ordinal),
                        $"{tour.Id}[{i}]: \"{target}\" does not resolve on {path}");
            }
        }
    }

    /// <summary>
    /// Three targets only render when there is something to show: the dashboard Attention card, and the
    /// profile's resume list and AI row. Seeded so the selector check covers them instead of skipping.
    /// </summary>
    private async Task SeedConditionalTargets(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ResumeVersions.Add(new ResumeVersion
        {
            UserId = userId, VersionNumber = 1, OriginalFileName = "resume.pdf",
            StoredPath = $"resumes/{userId}/1.pdf", FileSize = 1, IsActive = true, Label = "General"
        });
        db.JobApplications.Add(new JobApplication
        {
            UserId = userId, CompanyName = "Tour Co", RoleTitle = "Intern",
            Status = ApplicationStatus.Saved,
            Deadline = TestClock.Today.AddDays(-3)      // overdue → the Attention card renders
        });
        await db.SaveChangesAsync();
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
