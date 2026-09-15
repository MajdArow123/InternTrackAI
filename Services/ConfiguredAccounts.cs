using Microsoft.AspNetCore.Identity;

namespace InternTrackAI.Services;

/// <summary>How a configured account was found by <see cref="ConfiguredAccounts.FindAsync"/>.</summary>
public enum ConfiguredAccountMatch
{
    /// <summary>By <c>NormalizedEmail</c> (the normal case).</summary>
    Email,
    /// <summary>By <c>NormalizedUserName</c>, the lookup sign-in uses, because the email lookup found nothing.</summary>
    UserName
}

/// <summary>
/// The one way to read and match the accounts named in configuration (<c>Demo:Email</c>, <c>Admin:Email</c>).
/// Values set as Railway variables can pick up surrounding whitespace or a trailing newline. Identity's lookups
/// upper-case but never trim, so <c>"demo@x.com "</c> matches nobody on either provider. Every read is therefore
/// trimmed, and every comparison ignores case and surrounding whitespace on both sides. Database lookups go
/// through <see cref="UserManager{TUser}"/>, which compares the normalised (upper-cased) columns, so casing never
/// depends on the provider. Never compare <c>IdentityUser.Email</c> with <c>==</c> in a query: that is
/// case-sensitive on PostgreSQL and SQLite alike.
/// </summary>
public static class ConfiguredAccounts
{
    public const string DemoEmailKey  = "Demo:Email";
    public const string AdminEmailKey = "Admin:Email";

    /// <summary>Shown wherever an action is switched off for the shared demo account.</summary>
    public const string DemoUnavailableMessage = "Not available on the demo account.";

    /// <summary>True when the signed-in principal is the shared demo account (its email claim, else its user name).</summary>
    public static bool IsDemoUser(System.Security.Claims.ClaimsPrincipal? user, IConfiguration config)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        var demo = Read(config, DemoEmailKey);
        return Matches(user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value, demo) || Matches(user.Identity.Name, demo);
    }

    /// <summary>The trimmed value of <paramref name="key"/>, or null when it is missing or blank.</summary>
    public static string? Read(IConfiguration config, string key)
    {
        var value = config[key]?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>True when both addresses are present and equal ignoring case and surrounding whitespace.</summary>
    public static bool Matches(string? email, string? configured)
    {
        var a = email?.Trim();
        var b = configured?.Trim();
        return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="email"/> is the account configured under <paramref name="key"/>.</summary>
    public static bool IsConfigured(string? email, IConfiguration config, string key) => Matches(email, Read(config, key));

    /// <summary>
    /// Resolves a configured address to its Identity user: by normalised email first, then by normalised user
    /// name. Registration sets UserName = Email, and sign-in looks users up by name, so the fallback finds the
    /// account a person can actually sign in with even when its NormalizedEmail column is missing or stale.
    /// </summary>
    public static async Task<(IdentityUser? User, ConfiguredAccountMatch Match)> FindAsync(UserManager<IdentityUser> users, string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return (null, ConfiguredAccountMatch.Email);

        var byEmail = await users.FindByEmailAsync(trimmed);
        if (byEmail is not null) return (byEmail, ConfiguredAccountMatch.Email);

        return (await users.FindByNameAsync(trimmed), ConfiguredAccountMatch.UserName);
    }
}
