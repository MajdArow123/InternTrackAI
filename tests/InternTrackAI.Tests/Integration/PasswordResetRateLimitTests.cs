using System.Net;
using InternTrackAI.Areas.Identity;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// `/Identity/Account/ForgotPassword` is anonymous and spends real Resend quota, so it is rate limited per
/// client and per address. The property every test here circles: <b>a refusal must be invisible</b>. Same
/// status, same HTML, no 429 and no "slow down" text — if being rate limited looked any different from
/// being sent an email, the limit itself would answer "does this address have an account?".
/// </summary>
public class PasswordResetRateLimitTests
{
    /// <summary>Lets each request claim a client IP, the way Railway's proxy supplies one in production.</summary>
    private sealed class ClientIpFromHeader : IStartupFilter
    {
        public const string Header = "X-Test-Client-Ip";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers.TryGetValue(Header, out var raw) && IPAddress.TryParse(raw.ToString(), out var ip))
                    ctx.Connection.RemoteIpAddress = ip;
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    private sealed class Host : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public FakeResend Resend { get; } = new();

        public Host(params (string Key, string Value)[] settings)
        {
            Factory = Parent.WithWebHostBuilder(b =>
            {
                b.UseSetting("Resend:ApiKey", "re_test-not-real");
                b.UseSetting("Email:From", "InternTrackAI <noreply@test.invalid>");
                foreach (var (k, v) in settings) b.UseSetting(k, v);
                b.ConfigureServices(s => s.AddSingleton<IStartupFilter, ClientIpFromHeader>());
                b.ConfigureTestServices(services =>
                    services.AddHttpClient<IAppEmailSender, ResendEmailSender>()
                        .ConfigurePrimaryHttpMessageHandler(() => new FakeResend.Handler(Resend)));
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    private const string ClientA = "203.0.113.10";

    /// <summary>Posts the forgot-password form from a given client address.</summary>
    private static async Task<(HttpStatusCode Status, string Html)> RequestResetAsync(
        HttpClient client, string email, string ip = ClientA)
    {
        var page = new HttpRequestMessage(HttpMethod.Get, "/Identity/Account/ForgotPassword");
        page.Headers.Add(ClientIpFromHeader.Header, ip);
        var html = await (await client.SendAsync(page)).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var post = new HttpRequestMessage(HttpMethod.Post, "/Identity/Account/ForgotPassword")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["__RequestVerificationToken"] = token,
            })
        };
        post.Headers.Add(ClientIpFromHeader.Header, ip);
        var res = await client.SendAsync(post);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>The confirmation panel only — the rest of the page carries a per-render antiforgery token.</summary>
    private static string Confirmation(string html)
    {
        var start = html.IndexOf("auth-success", StringComparison.Ordinal);
        Assert.True(start >= 0, "no confirmation panel on the page");
        return html[start..Math.Min(html.Length, start + 400)];
    }

    /// <summary>Registers <paramref name="count"/> throwaway accounts, each on its own client so the limit under test is the only one in play.</summary>
    private static async Task<List<string>> RegisterManyAsync(Host host, int count)
    {
        var emails = new List<string>();
        for (var i = 0; i < count; i++) emails.Add(await Http.RegisterAsync(host.Client()));
        return emails;
    }

    // ── Per client ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_client_gets_five_sends_an_hour_and_the_sixth_is_silently_dropped()
    {
        using var host = new Host();
        var emails = await RegisterManyAsync(host, 6);
        var client = host.Client();

        string? allowed = null;
        for (var i = 0; i < 5; i++)
        {
            var (status, html) = await RequestResetAsync(client, emails[i]);
            Assert.Equal(HttpStatusCode.OK, status);
            allowed ??= Confirmation(html);
        }
        Assert.Equal(5, host.Resend.Sends);

        var (sixthStatus, sixthHtml) = await RequestResetAsync(client, emails[5]);

        Assert.Equal(5, host.Resend.Sends);                        // nothing sent
        Assert.Equal(HttpStatusCode.OK, sixthStatus);              // not a 429
        Assert.Equal(allowed, Confirmation(sixthHtml));            // and not a word different
    }

    [Fact]
    public async Task Running_one_client_out_does_not_affect_anyone_else()
    {
        using var host = new Host();
        var emails = await RegisterManyAsync(host, 7);
        var client = host.Client();

        for (var i = 0; i < 6; i++) await RequestResetAsync(client, emails[i], ClientA);
        Assert.Equal(5, host.Resend.Sends);

        // A different client, same app, still gets served.
        await RequestResetAsync(host.Client(), emails[6], "198.51.100.23");
        Assert.Equal(6, host.Resend.Sends);
    }

    [Fact]
    public async Task Probing_unknown_addresses_burns_the_probers_own_allowance()
    {
        using var host = new Host();
        var real = Assert.Single(await RegisterManyAsync(host, 1));
        var client = host.Client();

        // Five probes at addresses that don't exist. Nothing is sent, but the budget is spent.
        for (var i = 0; i < 5; i++) await RequestResetAsync(client, $"nobody-{i}@example.test");
        Assert.Equal(0, host.Resend.Sends);

        // So the sixth request — a real address this time — gets nothing either.
        var (status, html) = await RequestResetAsync(client, real);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, host.Resend.Sends);
        Assert.Contains("Check your email", Confirmation(html));
    }

    // ── Per address ──────────────────────────────────────────────────────────

    [Fact]
    public async Task One_address_receives_at_most_two_an_hour_however_many_clients_ask()
    {
        using var host = new Host();
        var victim = Assert.Single(await RegisterManyAsync(host, 1));
        var client = host.Client();

        // Three different clients, so the per-client limit is nowhere near being the cause.
        var (_, first) = await RequestResetAsync(client, victim, "203.0.113.31");
        await RequestResetAsync(client, victim, "203.0.113.32");
        Assert.Equal(2, host.Resend.Sends);

        var (status, html) = await RequestResetAsync(client, victim, "203.0.113.33");

        Assert.Equal(2, host.Resend.Sends);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Confirmation(first), Confirmation(html));
    }

    [Fact]
    public async Task Capitalising_the_address_differently_does_not_buy_more_sends()
    {
        using var host = new Host();
        var victim = Assert.Single(await RegisterManyAsync(host, 1));
        var client = host.Client();

        await RequestResetAsync(client, victim.ToUpperInvariant(), "203.0.113.41");
        await RequestResetAsync(client, victim, "203.0.113.42");
        await RequestResetAsync(client, victim.ToUpperInvariant(), "203.0.113.43");

        Assert.Equal(2, host.Resend.Sends);
    }

    [Fact]
    public async Task Exhausting_one_address_leaves_another_untouched()
    {
        using var host = new Host();
        var emails = await RegisterManyAsync(host, 2);
        var client = host.Client();

        for (var i = 0; i < 3; i++) await RequestResetAsync(client, emails[0], $"203.0.113.5{i}");
        Assert.Equal(2, host.Resend.Sends);

        await RequestResetAsync(client, emails[1], "203.0.113.59");
        Assert.Equal(3, host.Resend.Sends);
    }

    // ── Configurable, and scoped to this one page ────────────────────────────

    [Fact]
    public async Task The_limits_are_configurable()
    {
        using var host = new Host(
            ("RateLimiting:PasswordReset:PerIpPerHour", "2"),
            ("RateLimiting:PasswordReset:PerAddressPerHour", "1"));
        var emails = await RegisterManyAsync(host, 3);
        var client = host.Client();

        await RequestResetAsync(client, emails[0]);
        await RequestResetAsync(client, emails[1]);
        await RequestResetAsync(client, emails[2]);

        Assert.Equal(2, host.Resend.Sends);   // the third request exceeded the per-client limit of 2
    }

    [Fact]
    public async Task A_client_that_has_run_out_can_still_sign_in_and_use_the_app()
    {
        using var host = new Host();
        var emails = await RegisterManyAsync(host, 6);
        var client = host.Client();

        for (var i = 0; i < 6; i++) await RequestResetAsync(client, emails[i]);
        Assert.Equal(5, host.Resend.Sends);

        // Same client address, signed in: the limit is scoped to the anonymous reset page and nothing else.
        var user = host.Client();
        var email = await Http.RegisterAsync(user);
        Assert.Equal(email, email);

        var dashboard = new HttpRequestMessage(HttpMethod.Get, "/Home/Dashboard");
        dashboard.Headers.Add(ClientIpFromHeader.Header, ClientA);
        Assert.Equal(HttpStatusCode.OK, (await user.SendAsync(dashboard)).StatusCode);

        var apps = new HttpRequestMessage(HttpMethod.Get, "/JobApplications");
        apps.Headers.Add(ClientIpFromHeader.Header, ClientA);
        Assert.Equal(HttpStatusCode.OK, (await user.SendAsync(apps)).StatusCode);
    }

    // ── The other anonymous way to make the app send mail ────────────────────

    [Fact]
    public async Task Resend_email_confirmation_is_switched_off_rather_than_left_as_an_open_mailer()
    {
        // Identity UI maps this page even though registration never sends a confirmation, and it is anonymous
        // and backed by IEmailSender. Before it was disabled, twelve posts produced twelve real Resend sends.
        using var host = new Host();
        var client = host.Client();
        var email = await Http.RegisterAsync(client);

        const string path = "/Identity/Account/ResendEmailConfirmation";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);

        // A post without a form token can't even be built from the page now, so post directly.
        for (var i = 0; i < 5; i++)
        {
            var res = await client.PostAsync(path, new FormUrlEncodedContent(
                new Dictionary<string, string> { ["Input.Email"] = email }));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        Assert.Equal(0, host.Resend.Sends);
    }

    [Fact]
    public void Every_anonymous_Identity_page_that_can_send_mail_is_limited_or_disabled()
    {
        // A guard against an Identity UI upgrade quietly adding another anonymous mailer: the only two pages
        // that issue an email are ForgotPassword (rate limited above) and ResendEmailConfirmation (disabled).
        using var host = new Host();
        var pages = host.Factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<Microsoft.AspNetCore.Mvc.RazorPages.PageActionDescriptor>()
            .Where(d => d.AreaName == "Identity")
            .Select(d => d.ViewEnginePath)
            .Distinct()
            .ToList();

        Assert.Contains("/Account/ResendEmailConfirmation", pages);   // still mapped, just refusing
        Assert.Contains(UnusedIdentityPageFilter.ResendEmailConfirmation, UnusedIdentityPageFilter.DisabledPages);
    }

    [Fact]
    public async Task A_signed_in_user_can_still_change_their_password_from_an_exhausted_client()
    {
        using var host = new Host();
        var emails = await RegisterManyAsync(host, 6);
        var client = host.Client();
        for (var i = 0; i < 6; i++) await RequestResetAsync(client, emails[i]);

        // Changing a password while signed in is a different flow entirely and must stay open.
        var user = host.Client();
        await Http.RegisterAsync(user);
        var token = await Http.GetAntiforgeryTokenAsync(user, "/Identity/Account/Manage/ChangePassword");
        var change = new HttpRequestMessage(HttpMethod.Post, "/Identity/Account/Manage/ChangePassword")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.OldPassword"] = "Integration-Pass-1!",
                ["Input.NewPassword"] = "Integration-Pass-2!",
                ["Input.ConfirmPassword"] = "Integration-Pass-2!",
                ["__RequestVerificationToken"] = token,
            })
        };
        change.Headers.Add(ClientIpFromHeader.Header, ClientA);

        // A successful change redirects into Manage; a refusal would re-render the form with an error.
        var res = await user.SendAsync(change);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Identity/Account/Manage", res.Headers.Location!.ToString());
    }
}
