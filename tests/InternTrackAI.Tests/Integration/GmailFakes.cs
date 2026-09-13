using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InternTrackAI.Tests.Integration;

/// <summary>Scripted stand-in for Google's OAuth server: records every call, never touches the network.</summary>
public sealed class FakeGoogleOAuthClient : IGoogleOAuthClient
{
    public const string AccessToken  = "ya29.fake-access-token";
    public const string RefreshToken = "1//fake-refresh-token";

    public List<string> ExchangedCodes { get; } = new();
    public List<string> RefreshedTokens { get; } = new();
    public List<string> RevokedTokens { get; } = new();
    public string? LastState { get; private set; }
    public string? LastRedirectUri { get; private set; }
    public Exception? RevokeFailure { get; set; }

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        LastState = state;
        LastRedirectUri = redirectUri;
        return "https://accounts.google.test/o/oauth2/v2/auth?state=" + Uri.EscapeDataString(state) + "&redirect_uri=" + Uri.EscapeDataString(redirectUri);
    }

    public Task<GoogleTokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        ExchangedCodes.Add(code);
        return Task.FromResult(new GoogleTokens(AccessToken, RefreshToken, DateTime.UtcNow.AddHours(1)));
    }

    public Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        RefreshedTokens.Add(refreshToken);
        return Task.FromResult(new GoogleTokens(AccessToken + ".refreshed", null, DateTime.UtcNow.AddHours(1)));
    }

    public Task RevokeAsync(string token, CancellationToken ct = default)
    {
        RevokedTokens.Add(token);
        if (RevokeFailure is not null) throw RevokeFailure;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory Gmail: a profile address and a list of messages the sync will "find".</summary>
public sealed class FakeGmailClient : IGmailClient
{
    public string ProfileEmail { get; set; } = "student@gmail.test";
    public List<GmailMessage> Messages { get; } = new();
    public List<string> Queries { get; } = new();
    public List<string> AccessTokensSeen { get; } = new();

    public Task<string?> GetProfileEmailAsync(string accessToken, CancellationToken ct = default)
    {
        AccessTokensSeen.Add(accessToken);
        return Task.FromResult<string?>(ProfileEmail);
    }

    public Task<IReadOnlyList<string>> ListMessageIdsAsync(string accessToken, string query, int max, CancellationToken ct = default)
    {
        AccessTokensSeen.Add(accessToken);
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<string>>(Messages.Select(m => m.Id).Take(max).ToList());
    }

    public Task<GmailMessage?> GetMessageAsync(string accessToken, string id, int maxBodyChars, CancellationToken ct = default)
    {
        var m = Messages.FirstOrDefault(x => x.Id == id);
        return Task.FromResult(m is null ? null : m with { Body = m.Body.Length > maxBodyChars ? m.Body[..maxBodyChars] : m.Body });
    }
}

/// <summary>Boots the app with Google configured and both Google-facing clients replaced by the fakes above.</summary>
public static class GmailTestHost
{
    public static (TestAppFactory Parent, WebApplicationFactory<Program> Factory, FakeGoogleOAuthClient OAuth, FakeGmailClient Gmail) Boot(
        bool configured = true, params (string Key, string Value)[] settings)
    {
        var oauth = new FakeGoogleOAuthClient();
        var gmail = new FakeGmailClient();
        var parent = new TestAppFactory();
        var factory = parent.WithWebHostBuilder(b =>
        {
            if (configured)
            {
                b.UseSetting("Google:ClientId", "test-client-id.apps.googleusercontent.com");
                b.UseSetting("Google:ClientSecret", "test-client-secret");
            }
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IGoogleOAuthClient>();
                services.RemoveAll<IGmailClient>();
                services.AddSingleton<IGoogleOAuthClient>(oauth);
                services.AddSingleton<IGmailClient>(gmail);
            });
        });
        return (parent, factory, oauth, gmail);
    }

    public static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
