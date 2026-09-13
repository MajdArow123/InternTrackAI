namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Backs <c>Profile/Bookmarklet</c>, the install page for the "Save to InternTrackAI" bookmarklet.
/// Built by <c>ProfileController.Bookmarklet</c> from the current request's scheme and host so the
/// generated code points at whichever origin is serving the page (localhost, Railway, a custom
/// domain) with no configuration.
/// </summary>
public class BookmarkletViewModel
{
    /// <summary>Origin of the running site, e.g. <c>https://interntrackai.up.railway.app</c> (no trailing slash).</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Absolute URL of the capture endpoint the bookmarklet opens, e.g. <c>{BaseUrl}/Capture</c>.</summary>
    public string CaptureUrl => BaseUrl + "/Capture";

    /// <summary>
    /// The complete <c>javascript:</c> URL for the bookmark. It reads only <c>location.href</c> and
    /// <c>document.title</c>, URL-encodes both, and opens <see cref="CaptureUrl"/> in a new tab so the
    /// job-board tab stays put. No site-specific scraping happens client-side — the server does the work.
    /// </summary>
    public string Code =>
        "javascript:(function(){window.open('" + CaptureUrl +
        "?url='+encodeURIComponent(location.href)+'&title='+encodeURIComponent(document.title),'_blank');})();";

    /// <summary>Label shown on the draggable button; it becomes the bookmark's name when dropped on the bar.</summary>
    public const string Label = "Save to InternTrackAI";
}
