using System.Net;
using System.Text;
using System.Text.Json;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// The forgot-password flow end to end with Resend faked at the HTTP layer. The theme running through all
/// of these: the page must answer identically whether the address is unknown, is the demo account, or is
/// real and the send failed — otherwise it becomes a way to test which addresses have accounts here.
/// </summary>
public class PasswordResetEmailTests
{
    private const string DemoEmail = "demo@interntrack.test";

    /// <summary>Records every Resend request and answers from a scripted queue (last reply repeats).</summary>
    private sealed class FakeResend
    {
        public List<string> Bodies { get; } = new();
        public List<Func<HttpResponseMessage>> Replies { get; } = new();

        public JsonElement Payload(int index = 0) => JsonDocument.Parse(Bodies[index]).RootElement;

        public void AlwaysFail(HttpStatusCode status) => Replies.Add(() => new HttpResponseMessage(status)
        {
            Content = new StringContent($"{{\"statusCode\":{(int)status},\"name\":\"application_error\",\"message\":\"boom\"}}",
                Encoding.UTF8, "application/json")
        });

        public sealed class Handler(FakeResend state) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                int count;
                lock (state.Bodies) { state.Bodies.Add(body); count = state.Bodies.Count; }

                if (state.Replies.Count == 0)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"id\":\"stub-0001\"}", Encoding.UTF8, "application/json") };

                return state.Replies[Math.Min(count - 1, state.Replies.Count - 1)]();
            }
        }
    }

    private sealed class Host : IDisposable
    {
        public TestAppFactory Parent { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public FakeResend Resend { get; } = new();
        public CapturingLogger<ResendEmailSender> Log { get; } = new();

        /// <param name="withKey">false boots the app exactly as local development does: no Resend key at all.</param>
        public Host(bool withKey = true, params (string Key, string Value)[] settings)
        {
            Factory = Parent.WithWebHostBuilder(b =>
            {
                if (withKey)
                {
                    b.UseSetting("Resend:ApiKey", "re_test-not-real");
                    b.UseSetting("Email:From", "InternTrackAI <noreply@test.invalid>");
                }
                foreach (var (k, v) in settings) b.UseSetting(k, v);
                b.ConfigureTestServices(services =>
                {
                    if (withKey)
                        services.AddHttpClient<IAppEmailSender, ResendEmailSender>()
                            .ConfigurePrimaryHttpMessageHandler(() => new FakeResend.Handler(Resend));
                    services.RemoveAll<ILogger<ResendEmailSender>>();
                    services.AddSingleton<ILogger<ResendEmailSender>>(Log);
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public T Resolve<T>() where T : notnull
        {
            using var scope = Factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<T>();
        }

        public void Dispose() { Factory.Dispose(); Parent.Dispose(); }
    }

    /// <summary>Posts the forgot-password form and returns the rendered page.</summary>
    private static async Task<(HttpStatusCode Status, string Html)> RequestResetAsync(HttpClient client, string email)
    {
        var token = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/ForgotPassword");
        var res = await client.PostAsync("/Identity/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = token,
        }));
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>The confirmation panel, with the antiforgery token stripped so two renders can be compared.</summary>
    private static string Confirmation(string html)
    {
        var start = html.IndexOf("auth-success", StringComparison.Ordinal);
        return start < 0 ? "" : html[start..Math.Min(html.Length, start + 300)];
    }

    // ── Registration ─────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_Resend_key_the_console_sender_is_registered()
    {
        using var host = new Host(withKey: false);

        Assert.IsType<ConsoleEmailSender>(host.Resolve<IAppEmailSender>());
        // Identity UI's own pages resolve the plain interface; it has to reach the same implementation.
        Assert.IsType<ConsoleEmailSender>(host.Resolve<IEmailSender>());
    }

    [Fact]
    public void With_a_Resend_key_the_Resend_sender_is_registered()
    {
        using var host = new Host();

        Assert.IsType<ResendEmailSender>(host.Resolve<IAppEmailSender>());
        Assert.IsType<ResendEmailSender>(host.Resolve<IEmailSender>());
    }

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_reset_request_sends_one_email_carrying_the_reset_link()
    {
        using var host = new Host();
        var client = host.Client();
        var email = await Http.RegisterAsync(client);

        var (status, html) = await RequestResetAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Check your email", html);

        Assert.Single(host.Resend.Bodies);
        var payload = host.Resend.Payload();
        Assert.Equal("InternTrackAI <noreply@test.invalid>", payload.GetProperty("from").GetString());
        Assert.Equal(email, payload.GetProperty("to")[0].GetString());
        Assert.Equal("Reset your InternTrackAI password", payload.GetProperty("subject").GetString());

        foreach (var part in new[] { payload.GetProperty("html").GetString()!, payload.GetProperty("text").GetString()! })
            Assert.Contains("/Identity/Account/ResetPassword?code=", part);
    }

    [Fact]
    public async Task The_emailed_link_actually_resets_the_password()
    {
        using var host = new Host();
        var client = host.Client();
        var email = await Http.RegisterAsync(client);

        await RequestResetAsync(client, email);

        // Pull the link out of the plain-text part, exactly as a reader would.
        var text = host.Resend.Payload().GetProperty("text").GetString()!;
        var link = text.Split('\n', StringSplitOptions.TrimEntries)
                       .Single(l => l.Contains("/Identity/Account/ResetPassword?code="));

        var page = await client.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        const string newPassword = "Reset-Pass-2!";
        var resetToken = await Http.GetAntiforgeryTokenAsync(client, link);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query)["code"]!;
        var reset = await client.PostAsync(link, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword,
            ["Input.Code"] = code,
            ["__RequestVerificationToken"] = resetToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains("ResetPasswordConfirmation", reset.Headers.Location!.ToString());

        // And the new password actually signs in.
        var fresh = host.Client();
        var loginToken = await Http.GetAntiforgeryTokenAsync(fresh, "/Identity/Account/Login");
        var login = await fresh.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = newPassword,
            ["__RequestVerificationToken"] = loginToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    // ── The page never varies ────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_send_leaves_the_page_and_the_flow_unchanged()
    {
        using var host = new Host();
        host.Resend.AlwaysFail(HttpStatusCode.InternalServerError);
        var client = host.Client();
        var email = await Http.RegisterAsync(client);

        var (status, html) = await RequestResetAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Check your email", html);
        Assert.Equal(2, host.Resend.Bodies.Count);          // tried, retried, gave up — without throwing
    }

    [Fact]
    public async Task An_unknown_address_shows_the_same_page_and_sends_nothing()
    {
        using var host = new Host();
        var client = host.Client();
        var real = await Http.RegisterAsync(client);

        var (_, knownHtml) = await RequestResetAsync(client, real);
        var (status, unknownHtml) = await RequestResetAsync(client, "nobody-here@example.test");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Confirmation(knownHtml), Confirmation(unknownHtml));
        Assert.Single(host.Resend.Bodies);                  // only the real one was sent
    }

    [Fact]
    public async Task A_reset_for_the_demo_address_sends_nothing_and_shows_the_same_page()
    {
        // Padded and upper-cased the way a Railway variable arrives; ConfiguredAccounts has to see through it.
        using var host = new Host(withKey: true, ("Demo:Email", "  " + DemoEmail.ToUpperInvariant() + "\n"));
        var client = host.Client();
        var real = await Http.RegisterAsync(client);
        await Http.RegisterAsync(host.Client(), DemoEmail);

        var (_, knownHtml) = await RequestResetAsync(client, real);
        var (status, demoHtml) = await RequestResetAsync(client, DemoEmail);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Confirmation(knownHtml), Confirmation(demoHtml));
        Assert.Single(host.Resend.Bodies);                  // the demo request added nothing
    }

    // ── Logging ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_identifying_is_logged_when_a_send_fails()
    {
        using var host = new Host();
        host.Resend.AlwaysFail(HttpStatusCode.InternalServerError);
        var client = host.Client();
        var email = await Http.RegisterAsync(client);

        await RequestResetAsync(client, email);

        var lines = string.Join("\n", host.Log.Lines);
        Assert.Contains("failed", lines);
        Assert.DoesNotContain(email, lines);
        Assert.DoesNotContain("code=", lines);
        Assert.DoesNotContain("<html", lines);
        Assert.DoesNotContain("re_test-not-real", lines);
    }
}
