using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The parts of accessibility that live in the rendered HTML, so they can be pinned here rather
/// than only in the browser suite. Three failures this covers were all real and all invisible to a
/// sighted mouse user: a <c>&lt;label for&gt;</c> aiming at an id that did not exist (the tag helper
/// wrote <c>for="ResumeVersionId"</c> while the select carried <c>id="resumeVersionSelect"</c>),
/// selects with no accessible name at all, and a focusable <c>&lt;tr&gt;</c> wrapped around a
/// checkbox and three link-buttons.
///
/// These are string checks against the markup, in the same spirit as <see cref="TourTests"/>:
/// no HTML parser is pulled in, so the assertions stay narrow and literal on purpose.
/// </summary>
public class AccessibleMarkupTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public AccessibleMarkupTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A signed-in client with one application, so the list and Edit pages have content.</summary>
    private async Task<(HttpClient Client, string ListHtml)> SeededAsync()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);
        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        var create = await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyName"] = "Axe Markup Co",
            ["RoleTitle"]   = "Accessibility Intern",
            ["Status"]      = "Applied",
            ["WorkMode"]    = "Remote",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var list = await client.GetAsync("/JobApplications?view=list");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        return (client, await list.Content.ReadAsStringAsync());
    }

    private static IEnumerable<string> LabelTargets(string html) =>
        Regex.Matches(html, "<label[^>]*\\bfor=\"([^\"]+)\"").Select(m => m.Groups[1].Value);

    private static HashSet<string> Ids(string html) =>
        Regex.Matches(html, "\\bid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public async Task No_label_points_at_an_element_that_does_not_exist()
    {
        var (client, listHtml) = await SeededAsync();
        var id = Regex.Match(listHtml, "data-app-id=\"(\\d+)\"").Groups[1].Value;

        var pages = new Dictionary<string, string>
        {
            ["/JobApplications?view=list"] = listHtml,
            ["/JobApplications/Create"]    = await (await client.GetAsync("/JobApplications/Create")).Content.ReadAsStringAsync(),
            [$"/JobApplications/Edit/{id}"] = await (await client.GetAsync($"/JobApplications/Edit/{id}")).Content.ReadAsStringAsync(),
            ["/Profile"]                    = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync(),
            ["/Identity/Account/Login"]     = await (await NewClient().GetAsync("/Identity/Account/Login")).Content.ReadAsStringAsync(),
            ["/Identity/Account/Register"]  = await (await NewClient().GetAsync("/Identity/Account/Register")).Content.ReadAsStringAsync(),
        };

        var orphans = new List<string>();
        foreach (var (url, html) in pages)
        {
            var ids = Ids(html);
            orphans.AddRange(LabelTargets(html).Where(t => !ids.Contains(t)).Select(t => $"{url}: <label for=\"{t}\">"));
        }

        Assert.True(orphans.Count == 0, "labels pointing at missing elements:\n  " + string.Join("\n  ", orphans));
    }

    [Theory]
    [InlineData("filterWorkMode")]
    [InlineData("filterSortBy")]
    [InlineData("filterSearch")]
    public async Task The_list_filters_are_labelled(string id)
    {
        var (_, html) = await SeededAsync();

        Assert.Contains($"id=\"{id}\"", html);
        Assert.Contains($"for=\"{id}\"", html);
    }

    [Theory]
    [InlineData("/JobApplications/Create")]
    [InlineData("/Profile")]
    public async Task Every_select_on_the_page_has_an_accessible_name(string url)
    {
        var (client, _) = await SeededAsync();
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var labelled = LabelTargets(html).ToHashSet(StringComparer.Ordinal);

        var unnamed = Regex.Matches(html, "<select[^>]*>")
            .Select(m => m.Value)
            .Where(tag => !tag.Contains("aria-label", StringComparison.OrdinalIgnoreCase))
            .Where(tag =>
            {
                var id = Regex.Match(tag, "\\bid=\"([^\"]+)\"").Groups[1].Value;
                return id.Length == 0 || !labelled.Contains(id);
            })
            .ToList();

        Assert.True(unnamed.Count == 0, $"selects with no accessible name on {url}:\n  " + string.Join("\n  ", unnamed));
    }

    [Fact]
    public async Task The_resume_picker_label_matches_the_id_the_scripts_use()
    {
        var (client, listHtml) = await SeededAsync();
        var id = Regex.Match(listHtml, "data-app-id=\"(\\d+)\"").Groups[1].Value;

        foreach (var url in new[] { "/JobApplications/Create", $"/JobApplications/Edit/{id}" })
        {
            var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
            // The id is load-bearing for application-form.js, so the label has to come to it.
            Assert.Contains("id=\"resumeVersionSelect\"", html);
            Assert.Contains("for=\"resumeVersionSelect\"", html);
            Assert.DoesNotContain("for=\"ResumeVersionId\"", html);
        }
    }

    [Fact]
    public async Task The_profile_email_field_is_associated_with_its_label()
    {
        var (client, _) = await SeededAsync();
        var html = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();

        Assert.Contains("id=\"profileEmail\"", html);
        Assert.Contains("for=\"profileEmail\"", html);
    }

    [Fact]
    public async Task Application_rows_are_not_focusable_and_do_not_nest_interactive_controls()
    {
        var (_, html) = await SeededAsync();

        var rowTag = Regex.Match(html, "<tr[^>]*class=\"app-row\"[^>]*>").Value;
        Assert.NotEqual(string.Empty, rowTag);
        // The row holds a checkbox and three link-buttons, so it must not be a focusable control
        // itself - that is exactly axe's nested-interactive rule.
        Assert.DoesNotContain("tabindex", rowTag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role=\"button\"", rowTag, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_row_still_offers_a_keyboard_way_into_the_drawer()
    {
        var (_, html) = await SeededAsync();

        // Removing tabindex from the row would strand keyboard users if nothing replaced it.
        var openers = Regex.Matches(html, "<button[^>]*class=\"[^\"]*row-open[^\"]*\"[^>]*>").Count;
        var rows    = Regex.Matches(html, "<tr[^>]*class=\"app-row\"").Count;

        Assert.True(rows > 0, "no application rows rendered");
        Assert.Equal(rows, openers);
        Assert.Contains("aria-label=\"Open details for Axe Markup Co, Accessibility Intern\"", html);
    }

    [Fact]
    public async Task Board_cards_stay_focusable_because_they_nest_nothing()
    {
        var (client, _) = await SeededAsync();
        var html = await (await client.GetAsync("/JobApplications/Board")).Content.ReadAsStringAsync();

        // The mirror of the row test: a board card has no interactive descendants, so role=button
        // plus tabindex is the right thing there and must not be "fixed" to match the table.
        var card = Regex.Match(html, "<article[^>]*class=\"board-card app-row\"[^>]*>", RegexOptions.Singleline).Value;
        Assert.NotEqual(string.Empty, card);
        Assert.Contains("tabindex=\"0\"", card);
        Assert.Contains("role=\"button\"", card);
    }

    [Fact]
    public async Task The_drawer_is_a_labelled_dialog_in_the_markup()
    {
        var (_, html) = await SeededAsync();

        Assert.Contains("id=\"app-drawer\"", html);
        Assert.Contains("role=\"dialog\"", html);
        Assert.Contains("aria-label=\"Application details\"", html);
        // aria-modal is added by app-drawer.js only while it is open, so it must not be in the
        // server markup - a permanently modal element would hide the page from assistive tech.
        var drawerTag = Regex.Match(html, "<div[^>]*id=\"app-drawer\"[^>]*>").Value;
        Assert.DoesNotContain("aria-modal", drawerTag, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aria-hidden=\"true\"", drawerTag);
    }
}
