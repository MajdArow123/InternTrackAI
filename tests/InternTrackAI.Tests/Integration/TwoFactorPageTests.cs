using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The four Identity UI library pages that threw a 500 without two-factor state now 404 instead — and, the
/// half that matters as much, still work when the state is there, so turning this on locked nobody out
/// (<see cref="Areas.Identity.TwoFactorStateFilter"/>).
/// </summary>
public class TwoFactorPageTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public TwoFactorPageTests(TestAppFactory factory) => _factory = factory;

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/Identity/Account/LoginWith2fa")]
    [InlineData("/Identity/Account/LoginWithRecoveryCode")]
    public async Task Anonymous_two_factor_sign_in_pages_404_without_a_sign_in_in_progress(string url)
    {
        // These were anonymous 500s: anyone could write Error lines to the production log in a loop.
        var client = NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
    }

    [Theory]
    [InlineData("/Identity/Account/Manage/Disable2fa")]
    [InlineData("/Identity/Account/Manage/GenerateRecoveryCodes")]
    public async Task Two_factor_manage_pages_404_for_a_user_without_two_factor(string url)
    {
        var client = NewClient();
        await Http.RegisterAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_user_with_two_factor_can_still_reach_every_page_in_the_flow()
    {
        var owner = NewClient();
        var email = await Http.RegisterAsync(owner);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            await users.ResetAuthenticatorKeyAsync(user);        // what EnableAuthenticator does before verifying a code
            await users.SetTwoFactorEnabledAsync(user, true);
        }

        // Signed in with 2FA on: the manage pages answer.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/Identity/Account/Manage/GenerateRecoveryCodes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/Identity/Account/Manage/Disable2fa")).StatusCode);

        // A fresh browser signing in with the password is handed to LoginWith2fa, which must render, not 404.
        var browser = NewClient();
        var token = await Http.GetAntiforgeryTokenAsync(browser, "/Identity/Account/Login");
        var login = await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Input.Email"] = email, ["Input.Password"] = "Integration-Pass-1!",
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("LoginWith2fa", login.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(login.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Identity/Account/LoginWithRecoveryCode")).StatusCode);
    }
}
