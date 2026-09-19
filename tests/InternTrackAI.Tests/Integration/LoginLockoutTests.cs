using System.Net;
using InternTrackAI.Areas.Identity.Pages.Account;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// One app that knows about a demo account, so the lockout rule and its one exemption can be tested
/// against the same host. The demo address is configured with the padding and casing a Railway
/// variable might carry, because that is exactly the shape <see cref="InternTrackAI.Services.ConfiguredAccounts"/>
/// exists to survive — a plain <c>==</c> here would silently stop exempting the demo account.
/// </summary>
public class LoginLockoutFixture : TestAppFactory, IAsyncLifetime
{
    public const string Password = "Integration-Pass-1!";
    public string DemoEmail { get; } = $"demo-{Guid.NewGuid():N}@example.test";
    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        App = WithWebHostBuilder(b => b.UseSetting("Demo:Email", "  " + DemoEmail.ToUpperInvariant() + "\n"));
        await Http.RegisterAsync(NewClient(), DemoEmail, Password);
    }

    public HttpClient NewClient() => App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<T> WithScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = App.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// Password guessing used to be unlimited: <c>lockoutOnFailure: false</c> meant every wrong password
/// was just another attempt, and each one cost the server a 100k-iteration PBKDF2 hash. Now five
/// failures lock the account for fifteen minutes. The tests pin the boundary in both directions —
/// four failures must not lock, five must — because an off-by-one either lets guessing continue or
/// locks people out a try early. The shared demo account is the deliberate exception: its password is
/// handed out by the landing page, so any visitor could otherwise lock it for everyone.
/// </summary>
public class LoginLockoutTests : IClassFixture<LoginLockoutFixture>
{
    private readonly LoginLockoutFixture _f;
    public LoginLockoutTests(LoginLockoutFixture f) => _f = f;

    private async Task<string> RegisteredAccountAsync()
    {
        var email = $"lock-{Guid.NewGuid():N}@example.test";
        await Http.RegisterAsync(_f.NewClient(), email, LoginLockoutFixture.Password);
        return email;
    }

    private async Task<(HttpStatusCode Status, string Body, string? Location)> AttemptAsync(string email, string password)
    {
        var client = _f.NewClient();
        var token  = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/Login");
        var res = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]    = email,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
        return (res.StatusCode, await res.Content.ReadAsStringAsync(), res.Headers.Location?.ToString());
    }

    private Task<int> MaxAttemptsAsync() =>
        _f.WithScope(sp => Task.FromResult(sp.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout.MaxFailedAccessAttempts));

    private Task<TimeSpan> WindowAsync() =>
        _f.WithScope(sp => Task.FromResult(sp.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout.DefaultLockoutTimeSpan));

    [Fact]
    public async Task Identity_is_configured_for_five_attempts_and_a_fifteen_minute_window()
    {
        Assert.Equal(5, await MaxAttemptsAsync());
        Assert.Equal(TimeSpan.FromMinutes(15), await WindowAsync());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_and_the_right_one_is_then_refused()
    {
        var email = await RegisteredAccountAsync();
        var max   = await MaxAttemptsAsync();

        for (var i = 0; i < max; i++)
        {
            var attempt = await AttemptAsync(email, $"wrong-{i}");
            Assert.Equal(HttpStatusCode.OK, attempt.Status); // re-rendered form, not a redirect
        }

        // The password is correct here. Being locked out has to win anyway, or the lock means nothing.
        var afterLock = await AttemptAsync(email, LoginLockoutFixture.Password);
        Assert.Equal(HttpStatusCode.OK, afterLock.Status);
        Assert.Null(afterLock.Location);
        Assert.Contains("Too many failed sign-in attempts", afterLock.Body);
        Assert.Contains("15 minutes", afterLock.Body);
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_the_account_and_success_clears_the_count()
    {
        var email = await RegisteredAccountAsync();
        var max   = await MaxAttemptsAsync();

        for (var i = 0; i < max - 1; i++) await AttemptAsync(email, $"wrong-{i}");

        var ok = await AttemptAsync(email, LoginLockoutFixture.Password);
        Assert.Equal(HttpStatusCode.Redirect, ok.Status);
        Assert.DoesNotContain("Too many failed sign-in attempts", ok.Body);

        // Identity resets AccessFailedCount on a successful sign-in; if it did not, the next four
        // failures would lock an account that had only just been used correctly.
        var count = await _f.WithScope(async sp =>
            (await sp.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!.AccessFailedCount);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task The_account_signs_in_again_once_the_lockout_window_has_passed()
    {
        var email = await RegisteredAccountAsync();
        var max   = await MaxAttemptsAsync();

        for (var i = 0; i < max; i++) await AttemptAsync(email, $"wrong-{i}");
        Assert.Contains("Too many failed sign-in attempts", (await AttemptAsync(email, LoginLockoutFixture.Password)).Body);

        // Waiting out fifteen real minutes is not a test. Identity decides "still locked?" purely by
        // comparing LockoutEnd with UtcNow, so moving that stamp into the past is the same state the
        // clock would reach, without the wait.
        await _f.WithScope(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<IdentityUser>>();
            var user  = (await users.FindByEmailAsync(email))!;
            Assert.True(await users.IsLockedOutAsync(user), "account should be locked before the window is rewound");
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddSeconds(-1));
            return true;
        });

        var afterWindow = await AttemptAsync(email, LoginLockoutFixture.Password);
        Assert.Equal(HttpStatusCode.Redirect, afterWindow.Status);
        Assert.Equal("/", afterWindow.Location);
    }

    [Fact]
    public async Task A_locked_out_account_is_still_reachable_through_password_reset()
    {
        var email = await RegisteredAccountAsync();
        var max   = await MaxAttemptsAsync();
        for (var i = 0; i < max; i++) await AttemptAsync(email, $"wrong-{i}");

        // The message tells people to reset their password, so that door has to be open.
        var client = _f.NewClient();
        var token  = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/ForgotPassword");
        var res = await client.PostAsync("/Identity/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.True(res.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, $"forgot-password returned {res.StatusCode}");
    }

    [Fact]
    public async Task The_shared_demo_account_never_locks_out()
    {
        var max = await MaxAttemptsAsync();

        // Well past the threshold: a visitor mistyping the demo password must not take it down for
        // everyone else looking at the portfolio.
        for (var i = 0; i < max * 2 + 2; i++)
        {
            var attempt = await AttemptAsync(_f.DemoEmail, $"wrong-{i}");
            Assert.DoesNotContain("Too many failed sign-in attempts", attempt.Body);
        }

        var ok = await AttemptAsync(_f.DemoEmail, LoginLockoutFixture.Password);
        Assert.Equal(HttpStatusCode.Redirect, ok.Status);

        var user = await _f.WithScope(async sp =>
            (await sp.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(_f.DemoEmail))!);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.True(user.LockoutEnd is null || user.LockoutEnd <= DateTimeOffset.UtcNow,
            $"demo account carries a lockout until {user.LockoutEnd}");
    }

    [Fact]
    public async Task An_app_with_no_demo_configured_locks_every_account()
    {
        // The exemption must be driven by Demo:Email, not by anything about the address itself.
        using var plain = new TestAppFactory();
        var app = plain.WithWebHostBuilder(_ => { });
        HttpClient NewClient() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var email = $"nodemo-{Guid.NewGuid():N}@example.test";
        await Http.RegisterAsync(NewClient(), email, LoginLockoutFixture.Password);

        for (var i = 0; i < 5; i++)
        {
            var client = NewClient();
            var token  = await Http.GetAntiforgeryTokenAsync(client, "/Identity/Account/Login");
            await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = email, ["Input.Password"] = $"wrong-{i}",
                ["__RequestVerificationToken"] = token,
            }));
        }

        var final = NewClient();
        var finalToken = await Http.GetAntiforgeryTokenAsync(final, "/Identity/Account/Login");
        var res = await final.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email, ["Input.Password"] = LoginLockoutFixture.Password,
            ["__RequestVerificationToken"] = finalToken,
        }));

        Assert.Contains("Too many failed sign-in attempts", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(15, "15 minutes")]
    [InlineData(1, "1 minute")]
    public void The_lockout_message_states_the_configured_window(int minutes, string expected)
    {
        var message = LoginModel.LockedOutMessage(TimeSpan.FromMinutes(minutes));
        Assert.Contains(expected, message);
        Assert.Contains("reset your password", message);
    }
}
