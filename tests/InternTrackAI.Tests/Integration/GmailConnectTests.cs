using System.Net;
using System.Web;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Connect → Google → Callback → Disconnect over the real MVC/Identity pipeline with a fake Google.
/// The state round-trip, owner scoping and at-rest encryption of the tokens are the points under test.
/// </summary>
public class GmailConnectTests
{
    /// <summary>Follows the Connect redirect and returns the state Google would echo back (the client keeps the state cookie).</summary>
    private static async Task<string> StartConnectAsync(HttpClient client)
    {
        var res = await client.GetAsync("/Integrations/Gmail/Connect");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith("https://accounts.google.test/", location);
        var q = HttpUtility.ParseQueryString(new Uri(location).Query);
        Assert.Contains("/Integrations/Gmail/Callback", q["redirect_uri"]);
        return q["state"]!;
    }

    private static async Task<string> UserIdAsync(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (await users.FindByEmailAsync(email))!.Id;
    }

    [Fact]
    public async Task Connect_then_callback_stores_an_encrypted_connection_for_the_signed_in_user()
    {
        var (parent, factory, oauth, gmail) = GmailTestHost.Boot();
        using var _ = parent; using var __ = factory;
        gmail.ProfileEmail = "alice@gmail.test";

        var alice = GmailTestHost.Client(factory);
        var aliceEmail = await Http.RegisterAsync(alice);
        var state = await StartConnectAsync(alice);

        var cb = await alice.GetAsync($"/Integrations/Gmail/Callback?code=4/auth-code&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        Assert.Equal("/Profile", cb.Headers.Location!.ToString());
        Assert.Equal(new[] { "4/auth-code" }, oauth.ExchangedCodes);
        Assert.Contains(FakeGoogleOAuthClient.AccessToken, gmail.AccessTokensSeen);

        var aliceId = await UserIdAsync(factory, aliceEmail);
        using var scope = factory.Services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.GmailConnections.SingleAsync();
        Assert.Equal(aliceId, row.UserId);
        Assert.Equal("alice@gmail.test", row.GmailAddress);
        Assert.Null(row.LastSyncedAt);
        Assert.True(row.TokenExpiresAt > DateTime.UtcNow.AddMinutes(30));

        // Tokens at rest: the raw columns never contain the plaintext, but the protector round-trips them.
        var raw = await db.Database.SqlQueryRaw<string>("SELECT \"AccessToken\" || '|' || \"RefreshToken\" AS \"Value\" FROM \"GmailConnections\"").SingleAsync();
        Assert.DoesNotContain(FakeGoogleOAuthClient.AccessToken, raw);
        Assert.DoesNotContain(FakeGoogleOAuthClient.RefreshToken, raw);
        Assert.DoesNotContain(FakeGoogleOAuthClient.AccessToken, row.AccessToken);
        Assert.DoesNotContain(FakeGoogleOAuthClient.RefreshToken, row.RefreshToken);
        var protector = scope.ServiceProvider.GetRequiredService<GmailTokenProtector>();
        Assert.Equal(FakeGoogleOAuthClient.AccessToken,  protector.Unprotect(row.AccessToken));
        Assert.Equal(FakeGoogleOAuthClient.RefreshToken, protector.Unprotect(row.RefreshToken));

        // The profile shows the connected state.
        var html = await (await alice.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("alice@gmail.test", html);
        Assert.Contains("gmailDisconnectBtn", html);
        Assert.Contains("Gmail connected", html);   // toast
    }

    [Fact]
    public async Task Callback_with_a_wrong_or_missing_state_is_rejected_and_stores_nothing()
    {
        var (parent, factory, oauth, _) = GmailTestHost.Boot();
        using var __ = parent; using var ___ = factory;

        var client = GmailTestHost.Client(factory);
        await Http.RegisterAsync(client);
        var state = await StartConnectAsync(client);

        // Wrong nonce for a valid cookie.
        var tampered = await client.GetAsync("/Integrations/Gmail/Callback?code=4/auth-code&state=not-the-nonce");
        Assert.Equal(HttpStatusCode.Redirect, tampered.StatusCode);
        Assert.Equal("/Profile", tampered.Headers.Location!.ToString());

        // The cookie is consumed by the failed attempt, so even the right nonce is refused afterwards.
        var replay = await client.GetAsync($"/Integrations/Gmail/Callback?code=4/auth-code&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);

        // No cookie at all (a forged link).
        var fresh = GmailTestHost.Client(factory);
        await Http.RegisterAsync(fresh);
        var forged = await fresh.GetAsync("/Integrations/Gmail/Callback?code=4/auth-code&state=anything");
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);

        Assert.Empty(oauth.ExchangedCodes);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().GmailConnections.CountAsync());

        var html = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("could not be verified", html);
    }

    [Fact]
    public async Task State_belongs_to_the_user_who_started_the_flow()
    {
        var (parent, factory, oauth, _) = GmailTestHost.Boot();
        using var __ = parent; using var ___ = factory;

        var alice = GmailTestHost.Client(factory);
        await Http.RegisterAsync(alice);
        var state = await StartConnectAsync(alice);
        var cookie = (await alice.GetAsync("/Integrations/Gmail/Connect")).Headers.GetValues("Set-Cookie").First(c => c.StartsWith(Controllers.IntegrationsController.StateCookie));
        var stateCookieValue = cookie.Split(';')[0];

        // Bob presents Alice's cookie + state.
        var bob = GmailTestHost.Client(factory);
        await Http.RegisterAsync(bob);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/Integrations/Gmail/Callback?code=4/auth-code&state={Uri.EscapeDataString(oauth.LastState!)}");
        req.Headers.Add("Cookie", stateCookieValue);
        var res = await bob.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Empty(oauth.ExchangedCodes);
        _ = state;
    }

    [Fact]
    public async Task Connection_rows_are_owner_scoped_and_disconnect_revokes_and_deletes_only_your_own()
    {
        var (parent, factory, oauth, gmail) = GmailTestHost.Boot();
        using var _ = parent; using var __ = factory;

        var alice = GmailTestHost.Client(factory);
        await Http.RegisterAsync(alice);
        gmail.ProfileEmail = "alice@gmail.test";
        var aliceState = await StartConnectAsync(alice);
        await alice.GetAsync($"/Integrations/Gmail/Callback?code=a&state={Uri.EscapeDataString(aliceState)}");

        var bob = GmailTestHost.Client(factory);
        await Http.RegisterAsync(bob);

        // Bob sees his own (disconnected) card, never Alice's address.
        var bobHtml = await (await bob.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("gmailConnectBtn", bobHtml);
        Assert.DoesNotContain("alice@gmail.test", bobHtml);

        // Bob's Disconnect is a no-op for Alice's row.
        var bobToken = await Http.GetAntiforgeryTokenAsync(bob, "/Profile");
        var bobDisc = await bob.PostAsync("/Integrations/Gmail/Disconnect", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = bobToken }));
        Assert.Equal(HttpStatusCode.Redirect, bobDisc.StatusCode);
        Assert.Empty(oauth.RevokedTokens);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().GmailConnections.CountAsync());

        // Alice's Disconnect revokes with Google and removes the row, even if Google errors.
        oauth.RevokeFailure = new HttpRequestException("already revoked");
        var aliceToken = await Http.GetAntiforgeryTokenAsync(alice, "/Profile");
        var aliceDisc = await alice.PostAsync("/Integrations/Gmail/Disconnect", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = aliceToken }));
        Assert.Equal(HttpStatusCode.Redirect, aliceDisc.StatusCode);
        Assert.Equal(new[] { FakeGoogleOAuthClient.RefreshToken }, oauth.RevokedTokens);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().GmailConnections.CountAsync());

        var aliceHtml = await (await alice.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("gmailConnectBtn", aliceHtml);
        Assert.Contains("Gmail disconnected", aliceHtml);
    }

    [Fact]
    public async Task Everything_is_hidden_and_404_when_Google_is_not_configured()
    {
        var (parent, factory, _, _) = GmailTestHost.Boot(configured: false);
        using var __ = parent; using var ___ = factory;

        var client = GmailTestHost.Client(factory);
        await Http.RegisterAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Integrations/Gmail/Connect")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Integrations/Gmail/Callback?code=x&state=y")).StatusCode);

        var html = await (await client.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("connectedAccountsCard", html);
        Assert.DoesNotContain("Connect Gmail", html);
    }

    [Fact]
    public async Task Demo_account_sees_a_disabled_connect_button_and_cannot_start_the_flow()
    {
        var demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
        var (parent, factory, oauth, _) = GmailTestHost.Boot(true, ("Demo:Email", demoEmail), ("Demo:Password", "x-Demo-1!"));
        using var __ = parent; using var ___ = factory;

        var demo = GmailTestHost.Client(factory);
        await Http.RegisterAsync(demo, demoEmail);

        var html = await (await demo.GetAsync("/Profile")).Content.ReadAsStringAsync();
        Assert.Contains("connectedAccountsCard", html);
        Assert.Contains("Not available on the demo account", html);
        Assert.Contains("id=\"gmailConnectBtn\" disabled", html);
        Assert.DoesNotContain("href=\"/Integrations/Gmail/Connect\"", html);

        var res = await demo.GetAsync("/Integrations/Gmail/Connect");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Profile", res.Headers.Location!.ToString());
        Assert.Null(oauth.LastState);
    }

    [Fact]
    public async Task Anonymous_requests_are_sent_to_login()
    {
        var (parent, factory, _, _) = GmailTestHost.Boot();
        using var __ = parent; using var ___ = factory;
        var client = GmailTestHost.Client(factory);

        var res = await client.GetAsync("/Integrations/Gmail/Connect");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Identity/Account/Login", res.Headers.Location!.ToString());
    }
}
