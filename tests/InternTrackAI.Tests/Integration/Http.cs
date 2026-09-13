using System.Net;
using System.Text.RegularExpressions;

namespace InternTrackAI.Tests.Integration;

/// <summary>Small helpers for driving the real Identity/MVC pages over HttpClient.</summary>
internal static class Http
{
    private static readonly Regex TokenRx = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        var m = TokenRx.Match(html);
        Assert.True(m.Success, $"No antiforgery token found on {url}");
        return m.Groups[1].Value;
    }

    /// <summary>Registers a fresh account through the Register page; the client keeps the auth cookie.</summary>
    public static async Task<string> RegisterAsync(HttpClient client, string? email = null, string password = "Integration-Pass-1!")
    {
        email ??= $"it-{Guid.NewGuid():N}@example.test";
        var token = await GetAntiforgeryTokenAsync(client, "/Identity/Account/Register");
        var res = await client.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"]           = email,
            ["Input.DisplayName"]     = "Integration Tester",
            ["Input.Password"]        = password,
            ["Input.ConfirmPassword"] = password,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return email;
    }
}
