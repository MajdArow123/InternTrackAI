namespace InternTrackAI.Services;

/// <summary>
/// The response headers every request gets, and the one place their values are written.
///
/// Three are enforced and safe to enforce: this app never frames itself, never relies on
/// content-type sniffing, and never needs to leak a full URL to another origin. The Content
/// Security Policy ships <b>report-only</b> on purpose — <c>_Layout.cshtml</c> carries the theme
/// pre-paint, toast and keyboard-shortcut scripts inline, and <c>Views/InterviewPrep/Prep.cshtml</c>
/// still holds ~170 inline lines (section 12), so an enforced <c>script-src 'self'</c> would break
/// the app today. Note the policy deliberately does <i>not</i> grant <c>'unsafe-inline'</c> for
/// scripts: report-only plus a strict directive is what surfaces each remaining inline block in the
/// browser console, which is the list to work through before this can be enforced. Grant it and the
/// report goes quiet while the exposure stays.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>Content sniffing off: uploads are served back to their owner from /uploads.</summary>
    public const string ContentTypeOptions = "nosniff";

    /// <summary>
    /// No framing at all. SAMEORIGIN would be enough for this app, but nothing here is ever framed,
    /// including by itself, so the stricter value costs nothing. Mirrored by <c>frame-ancestors</c>
    /// in the CSP for browsers that prefer it.
    /// </summary>
    public const string FrameOptions = "DENY";

    /// <summary>
    /// Full URL to same-origin navigation, bare origin when crossing to another HTTPS site, nothing
    /// on a downgrade. Application pages carry record ids in their paths and link out to job postings
    /// and GitHub, so the path must not ride along in the Referer.
    /// </summary>
    public const string ReferrerPolicy = "strict-origin-when-cross-origin";

    /// <summary>
    /// Everything this app actually loads, and nothing else. Worth reading against reality rather
    /// than copied from a template:
    /// <list type="bullet">
    /// <item><c>style-src</c>/<c>font-src</c> name the Google Fonts origins because the layouts load
    /// Inter from there — the only third-party origin in the whole app.</item>
    /// <item><c>img-src blob:</c> is required by the profile photo preview, which renders the picked
    /// file through <c>URL.createObjectURL</c> before upload (<c>wwwroot/js/profile.js</c>).</item>
    /// <item><c>data:</c> covers inline SVG and the favicon set.</item>
    /// <item><c>form-action 'self'</c> is safe for the Gmail OAuth flow: Connect leaves by a 302 to
    /// Google, which is a navigation, not a form post, and the callback returns to this origin.</item>
    /// <item><c>object-src 'none'</c> and <c>base-uri 'self'</c> close the two injection escapes a
    /// policy usually forgets.</item>
    /// </list>
    /// jsPDF downloads are unaffected: <c>doc.save()</c> hands a blob to an anchor with a download
    /// attribute, which no directive here governs.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self'";

    public const string ContentTypeOptionsHeader = "X-Content-Type-Options";
    public const string FrameOptionsHeader       = "X-Frame-Options";
    public const string ReferrerPolicyHeader     = "Referrer-Policy";
    public const string CspReportOnlyHeader      = "Content-Security-Policy-Report-Only";

    /// <summary>
    /// Adds the headers to every response, including static files, the health endpoint and the
    /// re-executed error pages.
    ///
    /// Two placement rules. It must come <b>after</b> <c>UseForwardedHeaders</c>, which section 8
    /// requires to stay first so the proxy's scheme and host are known before anything reads them —
    /// nothing here depends on that, but the ordering rule is absolute. And it must come
    /// <b>before</b> <c>UseStaticFiles</c>, which short-circuits: registered any later and CSS, JS
    /// and uploaded files would go out bare.
    ///
    /// The values are written in <c>OnStarting</c>, which runs as headers are flushed, so ours is
    /// what ships no matter what else in the pipeline writes the same header. That matters here
    /// because something in the Identity stack already emits <c>X-Frame-Options: SAMEORIGIN</c> on
    /// its pages — that was the only <c>X-Frame-Options</c> in the app before this middleware. Worth
    /// being precise about the reason, though: a version that writes the headers before
    /// <c>next()</c> also yields DENY on those pages today, so this is insurance against ordering
    /// rather than a fix for a collision anyone has observed. <c>SecurityHeaderTests</c> asserts the
    /// outcome, not the mechanism, so either implementation stays honest.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var headers = ((HttpContext)state).Response.Headers;
                headers[ContentTypeOptionsHeader] = ContentTypeOptions;
                headers[FrameOptionsHeader]       = FrameOptions;
                headers[ReferrerPolicyHeader]     = ReferrerPolicy;
                headers[CspReportOnlyHeader]      = ContentSecurityPolicy;
                return Task.CompletedTask;
            }, context);

            await next();
        });
}
