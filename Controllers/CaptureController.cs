using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InternTrackAI.Controllers;

/// <summary>
/// Target of the "Save to InternTrackAI" bookmarklet (see <c>Profile/Bookmarklet</c>). The
/// bookmarklet only ever opens <c>GET /Capture?url=…&amp;title=…</c> in a new tab, so this is a
/// plain authorized GET: no cross-site POST, no antiforgery token, and no CORS involved. An
/// anonymous visitor is bounced through the normal Identity login page and comes back to the same
/// URL (query string intact) after signing in.
/// </summary>
[Authorize]
public class CaptureController : Controller
{
    /// <summary>Longest URL the bookmarklet may hand us; anything longer is rejected outright.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>Configuration key for the analyzer time cap, in seconds (default <see cref="DefaultTimeoutSeconds"/>).</summary>
    public const string TimeoutSettingKey = "Capture:AnalyzeTimeoutSeconds";
    public const int DefaultTimeoutSeconds = 15;

    private readonly JobAnalyzerService _analyzer;
    private readonly IConfiguration _config;
    private readonly ILogger<CaptureController> _logger;

    public CaptureController(JobAnalyzerService analyzer, IConfiguration config, ILogger<CaptureController> logger)
    {
        _analyzer = analyzer;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Validates the posting URL, runs it through the existing AI job analyzer (capped at
    /// <see cref="TimeoutSettingKey"/> seconds), and redirects to the Create Application form with
    /// whatever was extracted pre-filled. If the analyzer fails or times out, the form still opens
    /// with the URL and page title filled in plus an info toast. Nothing is saved here — the user
    /// reviews the form and submits it themselves.
    /// </summary>
    /// <param name="url">Absolute http(s) URL of the job posting, at most 2048 characters.</param>
    /// <param name="title">The job board tab's document title (optional).</param>
    /// <returns>302 to <c>JobApplications/Create</c>, or 400 for a missing/invalid/unsafe URL.</returns>
    [HttpGet("/Capture")]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    public async Task<IActionResult> Index(string? url, string? title)
    {
        var uri = ValidateUrl(url);
        if (uri is null)
            return BadRequest("Capture needs a public http(s) job posting URL of at most 2048 characters.");

        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var prefill = new CapturePrefill { Url = uri.AbsoluteUri, Title = title };

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.GetValue(TimeoutSettingKey, DefaultTimeoutSeconds)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        cts.CancelAfter(timeout);

        JobAnalysisResult result;
        try
        {
            result = await _analyzer.AnalyzeAsync(uri.AbsoluteUri, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException || cts.IsCancellationRequested)
        {
            result = new JobAnalysisResult { Success = false, Error = "Timed out." };
        }

        if (result.Success)
        {
            prefill.Company  = result.CompanyName;
            prefill.Role     = result.RoleTitle;
            prefill.Location = result.Location;
            prefill.Salary   = result.Salary;
            prefill.WorkMode = GuessWorkMode(result.Location);
        }
        else
        {
            _logger.LogInformation("Capture fell back to manual entry for {Host}: {Error}", uri.Host, result.Error);
            TempData["Toast"] = "info|Couldn't read the posting automatically — fill in the details.";
        }

        return RedirectToAction("Create", "JobApplications", new
        {
            url      = prefill.Url,
            title    = prefill.Title,
            company  = prefill.Company,
            role     = prefill.Role,
            location = prefill.Location,
            salary   = prefill.Salary,
            deadline = prefill.Deadline?.ToString("yyyy-MM-dd"),
            workMode = prefill.WorkMode?.ToString()
        });
    }

    /// <summary>
    /// Accepts only absolute http/https URLs up to <see cref="MaxUrlLength"/> characters.
    /// <c>javascript:</c>, <c>data:</c>, <c>file:</c> and relative values are rejected — the URL
    /// ends up in an href on the Create page and is fetched server-side, so it must be a real web address.
    /// </summary>
    public static Uri? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.Length > MaxUrlLength) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return uri;
    }

    /// <summary>
    /// The analyzer reports location as free text ("Remote", "Hybrid — Austin, TX"); map the
    /// obvious cases onto the form's Work Mode select and otherwise leave the default alone.
    /// </summary>
    private static WorkMode? GuessWorkMode(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        if (location.Contains("remote", StringComparison.OrdinalIgnoreCase)) return WorkMode.Remote;
        if (location.Contains("hybrid", StringComparison.OrdinalIgnoreCase)) return WorkMode.Hybrid;
        return null;
    }
}
