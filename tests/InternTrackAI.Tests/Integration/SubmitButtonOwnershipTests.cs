using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Which form a submit button belongs to, and which button a page hands over first.
///
/// The nav's logout used to be wrapped in its own &lt;form&gt;, and the nav is the first thing in
/// the body, so <c>form button[type=submit]</c> resolved to Logout on every signed-in page. Enter
/// was never affected — implicit submission uses the form's own default button — but anything
/// reaching for "the submit button" got the wrong one, and the consequence was silently being
/// signed out. The button now sits outside any form and points at <c>#logout-form</c>, rendered
/// last, so it is not a form descendant at all.
/// </summary>
public class SubmitButtonOwnershipTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SubmitButtonOwnershipTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<(HttpClient Client, string Id)> SeededAsync()
    {
        var client = NewClient();
        await Http.RegisterAsync(client);
        var token = await Http.GetAntiforgeryTokenAsync(client, "/JobApplications/Create");
        await client.PostAsync("/JobApplications/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CompanyName"] = "Submit Owner Co",
            ["RoleTitle"]   = "Form Intern",
            ["Status"]      = "Applied",
            ["WorkMode"]    = "Remote",
            ["__RequestVerificationToken"] = token,
        }));
        var list = await client.GetAsync("/JobApplications?view=list");
        var html = await list.Content.ReadAsStringAsync();
        return (client, Regex.Match(html, "data-app-id=\"(\\d+)\"").Groups[1].Value);
    }

    /// <summary>The markup between a &lt;form&gt; open tag and the next one — near enough to ask
    /// "is this button inside a form?" without pulling in an HTML parser.</summary>
    private static bool LogoutButtonIsInsideAForm(string html)
    {
        var logout = html.IndexOf("navbar-logout-btn", StringComparison.Ordinal);
        if (logout < 0) return false;
        var formOpen  = html.LastIndexOf("<form", logout, StringComparison.Ordinal);
        var formClose = html.LastIndexOf("</form>", logout, StringComparison.Ordinal);
        return formOpen > formClose;   // an open tag with no close between it and the button
    }

    [Theory]
    [InlineData("/Home/Dashboard")]
    [InlineData("/JobApplications?view=list")]
    [InlineData("/Profile")]
    [InlineData("/JobApplications/Create")]
    [InlineData("/Identity/Account/Manage/DeletePersonalData")]
    public async Task The_logout_button_is_not_inside_a_form(string url)
    {
        var (client, _) = await SeededAsync();

        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.Contains("navbar-logout-btn", html);
        Assert.Contains("form=\"logout-form\"", html);
        Assert.False(LogoutButtonIsInsideAForm(html),
            $"the logout button is a form descendant on {url}, so it is that page's first form submit again");
    }

    [Fact]
    public async Task The_logout_form_is_rendered_once_at_the_end_of_the_document()
    {
        var (client, _) = await SeededAsync();
        var html = await (await client.GetAsync("/Home/Dashboard")).Content.ReadAsStringAsync();

        Assert.Single(Regex.Matches(html, "id=\"logout-form\""));
        // After every other form on the page, so it can never be the first one reached.
        var logoutForm = html.IndexOf("id=\"logout-form\"", StringComparison.Ordinal);
        var lastOther  = html.LastIndexOf("<form", logoutForm, StringComparison.Ordinal);
        Assert.True(logoutForm > lastOther, "another form is rendered after #logout-form");
    }

    [Fact]
    public async Task Logging_out_through_that_form_still_works()
    {
        var (client, _) = await SeededAsync();

        // The nav button posts here by its form attribute - no JavaScript involved.
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Home/Dashboard");
        var res = await client.PostAsync("/Identity/Account/Logout?returnUrl=%2F", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
        Assert.True(res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, $"logout returned {res.StatusCode}");

        var after = await client.GetAsync("/Home/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("Login", after.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_Edit_form_owns_its_own_save_button()
    {
        var (client, id) = await SeededAsync();
        var html = await (await client.GetAsync($"/JobApplications/Edit/{id}")).Content.ReadAsStringAsync();

        // Mark-contacted sits inside #appForm but belongs to #mark-contacted-form through form=,
        // which is correct HTML and keeps Enter on #appForm submitting Save changes. It does mean
        // "the first submit inside #appForm" is the wrong button, so the save has a stable id.
        Assert.Contains("id=\"saveApplicationBtn\"", html);
        Assert.Contains("form=\"mark-contacted-form\"", html);
        Assert.Contains("id=\"mark-contacted-form\"", html);

        var markContacted = html.IndexOf("id=\"markContactedBtn\"", StringComparison.Ordinal);
        var save          = html.IndexOf("id=\"saveApplicationBtn\"", StringComparison.Ordinal);
        Assert.True(markContacted > 0 && save > markContacted,
            "expected the Mark-contacted button to precede Save changes; if that changed, the id hook may no longer be needed");
    }
}
