using System.Net;
using InternTrackAI.Data;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// /Identity/Account/Register is anonymous, sends no confirmation email and signs the visitor
/// straight in, so the only thing standing between one client and an unbounded supply of accounts
/// is <see cref="RegistrationLimiter"/>. Both directions matter: the limit has to bite, and a normal
/// visitor registering once has to be untouched by it.
///
/// <para>TestServer gives every request a null RemoteIpAddress, so all of these share the one
/// "ip:unknown" bucket — which is exactly the shape the test wants. <see cref="TestAppFactory"/>
/// raises the limit far out of the way for the rest of the suite; each fact here builds its own host
/// with a small limit so it is testing a bucket nothing else has drawn from.</para>
/// </summary>
public class RegistrationRateLimitTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public RegistrationRateLimitTests(TestAppFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> WithLimit(int perIpPerHour) =>
        _factory.WithWebHostBuilder(b =>
            b.UseSetting("RateLimiting:Registration:PerIpPerHour", perIpPerHour.ToString()));

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> PostRegistrationAsync(HttpClient client, string email)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/Register");
        return await client.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]           = email,
            ["Input.Password"]        = "Integration-Pass-1!",
            ["Input.ConfirmPassword"] = "Integration-Pass-1!",
            ["__RequestVerificationToken"] = token,
        }));
    }

    [Fact]
    public async Task One_registration_still_just_works()
    {
        using var factory = WithLimit(5);
        var client = Client(factory);

        var res = await PostRegistrationAsync(client, $"rl-ok-{Guid.NewGuid():N}@example.test");

        // Redirect + auth cookie: the visitor is registered and signed in, as before the limit existed.
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var dashboard = await client.GetAsync("/Home/Dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task A_client_may_register_up_to_the_limit_and_is_refused_after_it()
    {
        const int limit = 3;
        using var factory = WithLimit(limit);
        var client = Client(factory);
        var emails = Enumerable.Range(0, limit + 2)
            .Select(i => $"rl-flood-{i}-{Guid.NewGuid():N}@example.test").ToArray();

        var statuses = new List<HttpStatusCode>();
        foreach (var email in emails)
            statuses.Add((await PostRegistrationAsync(client, email)).StatusCode);

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Redirect, limit), statuses.Take(limit));
        Assert.All(statuses.Skip(limit), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));

        // The refused posts created nothing: the permit is taken before CreateAsync, not after it.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var created = await db.Users.CountAsync(u => emails.Contains(u.Email));
        Assert.Equal(limit, created);
    }

    [Fact]
    public async Task A_refusal_says_why_and_when_to_come_back()
    {
        using var factory = WithLimit(1);
        var client = Client(factory);
        await PostRegistrationAsync(client, $"rl-msg-a-{Guid.NewGuid():N}@example.test");

        var refused = await PostRegistrationAsync(client, $"rl-msg-b-{Guid.NewGuid():N}@example.test");
        var html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        // The form comes back with the reason on it rather than the re-executed 404 the status-code
        // pages middleware would produce for a bodiless 429.
        Assert.Contains("Too many accounts have been created from this connection.", html);
        Assert.Contains("Input_Password", html);
    }

    [Fact]
    public void The_refusal_message_names_a_wait_when_the_limiter_gives_one()
    {
        Assert.Equal(
            "Too many accounts have been created from this connection. Try again in about 12 minutes.",
            InternTrackAI.Areas.Identity.Pages.Account.RegisterModel.RateLimitedMessage(TimeSpan.FromMinutes(11.4)));

        Assert.Equal(
            "Too many accounts have been created from this connection. Try again later.",
            InternTrackAI.Areas.Identity.Pages.Account.RegisterModel.RateLimitedMessage(null));
    }
}
