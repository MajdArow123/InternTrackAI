namespace InternTrackAI.Services;

/// <summary>
/// The origin an emailed link points at. Same shape as <c>Google:RedirectBaseUrl</c>: unset means "derive it
/// from the request", a value pins it verbatim.
///
/// <para>Reset links are otherwise built from <c>Request.Scheme</c>/<c>Request.Host</c>, which behind Railway
/// are whatever the forwarded headers said. That is correct as long as the proxy is the only thing that can
/// set those headers — but it is the one place where a host the app was merely *asked* about ends up inside a
/// message the app sends to somebody's mailbox. Pinning the origin takes the request out of the decision.</para>
///
/// <para><b>Why this may be pinned when <c>Google:RedirectBaseUrl</c> may not.</b> The app answers on two
/// hostnames at once and the OAuth state cookie is host-scoped, so forcing a consent flow begun on one host to
/// return to the other loses the cookie and fails the callback. A password-reset token is bound to the user
/// and nothing else: a link to the canonical host works whichever host asked for it. The only visible effect
/// of setting this is that a visitor who asked from the Railway hostname is sent to the custom domain.</para>
///
/// <para>Left unset — the default, and what the test suite runs on — behaviour is exactly as before.</para>
/// </summary>
public static class EmailLinkBase
{
    public const string ConfigKey = "Email:BaseUrl";

    /// <summary>
    /// The configured origin normalised to <c>scheme://host[:port][/path]</c> with no trailing slash, or
    /// <c>null</c> when it is unset or unusable. A value that is not an absolute http(s) URL is treated as
    /// unset rather than pasted into a link: a malformed origin here would ship in an email nobody can recall.
    /// Query and fragment are dropped — a base URL carrying either is a typo, not an intent.
    /// </summary>
    public static string? Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// Joins a resolved base to an app-relative path (<c>/Identity/Account/ResetPassword?code=…</c>, as
    /// <c>Url.Page</c> renders it).
    /// </summary>
    public static string Combine(string resolvedBase, string relativePath) =>
        resolvedBase + (relativePath.StartsWith('/') ? relativePath : "/" + relativePath);
}
