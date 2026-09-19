using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// `?ReturnUrl=` on the Login page is attacker-supplied: anyone can send a crafted link. Two
/// properties have to hold together. The redirect must never leave this origin — that part always
/// worked, because <c>LocalRedirect</c> refuses a foreign target. But it refuses by *throwing*, and
/// the sign-in has already happened by then, so a non-local value turned a successful login into a
/// 500 with the user authenticated behind an error page. The fix drops a non-local value in favour
/// of the home page; these tests pin both halves so a future edit can't trade one for the other.
/// </summary>
public class LoginReturnUrlTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public LoginReturnUrlTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private const string Password = "Integration-Pass-1!";

    /// <summary>Registers an account, then drops the cookie so the caller can log in from scratch.</summary>
    private async Task<string> RegisteredAccountAsync()
    {
        var setup = NewClient();
        var email = await Http.RegisterAsync(setup, password: Password);
        return email;
    }

    private async Task<HttpResponseMessage> LogInAsync(HttpClient client, string email, string returnUrl)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, $"/Identity/Account/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        return await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]    = email,
            ["Input.Password"] = Password,
            ["ReturnUrl"]      = returnUrl,
            ["__RequestVerificationToken"] = token,
        }));
    }

    [Theory]
    [InlineData("https://evil.example/phish")]   // absolute, another origin
    [InlineData("//evil.example/phish")]         // protocol-relative: a host, not a path
    [InlineData("http://evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]          // not a navigation target at all
    public async Task A_non_local_ReturnUrl_lands_on_the_home_page_instead_of_throwing(string hostile)
    {
        var email  = await RegisteredAccountAsync();
        var client = NewClient();

        var res = await LogInAsync(client, email, hostile);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.Equal("/", location);
        Assert.DoesNotContain("evil.example", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_non_local_ReturnUrl_still_signs_the_user_in()
    {
        var email  = await RegisteredAccountAsync();
        var client = NewClient();

        var login = await LogInAsync(client, email, "https://evil.example/phish");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        // The sign-in happened before the old code threw, so the session must survive the fallback:
        // the visitor should be logged in, not stranded on an error page.
        var dashboard = await client.GetAsync("/Home/Dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Theory]
    [InlineData("/JobApplications")]
    [InlineData("/JobApplications?view=list&status=Applied")]
    [InlineData("/Profile")]
    public async Task A_local_ReturnUrl_is_still_honoured(string local)
    {
        var email  = await RegisteredAccountAsync();
        var client = NewClient();

        var res = await LogInAsync(client, email, local);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(local, res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Login_with_no_ReturnUrl_goes_to_the_home_page()
    {
        var email  = await RegisteredAccountAsync();
        var client = NewClient();

        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/Login");
        var res = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]    = email,
            ["Input.Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location!.ToString());
    }

    /// <summary>
    /// The hidden ReturnUrl field must carry the sanitised value, not the raw query argument. It takes
    /// a <c>ModelState.Remove</c> in OnGetAsync to get there, because asp-for renders from ModelState
    /// before the page property — drop that line and this test fails while every other one here still
    /// passes. The walk-through then posts the form exactly as the browser would, so both halves are
    /// covered: what the page hands the user, and where the POST actually sends them.
    /// </summary>
    [Fact]
    public async Task A_hostile_ReturnUrl_is_sanitised_in_the_rendered_form_and_on_the_redirect()
    {
        var email  = await RegisteredAccountAsync();
        var client = NewClient();

        var page = await client.GetAsync("/Identity/Account/Login?ReturnUrl=" + Uri.EscapeDataString("https://evil.example/phish"));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var rendered = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"ReturnUrl\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
        Assert.Equal("/", rendered);
        Assert.DoesNotContain("evil.example", html, StringComparison.OrdinalIgnoreCase);

        var token = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var res = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]    = email,
            ["Input.Password"] = Password,
            ["ReturnUrl"]      = rendered,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location!.ToString());
    }
}
