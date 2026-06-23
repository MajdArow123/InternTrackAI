using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;

namespace InternTrackAI.Controllers;

/// <summary>
/// Serves the public marketing homepage, the authenticated dashboard with aggregated
/// application analytics, and the app's custom error/not-found pages.
/// </summary>
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _context;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    /// <summary>Renders the marketing/hero landing page. No model, no auth required.</summary>
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Builds the signed-in user's dashboard: total/per-status application counts, success rate,
    /// top companies by application count, deadlines in the next 3 days, and applications that
    /// have sat in "Applied" for 7+ days without movement (follow-up nudges).
    /// </summary>
    /// <returns>The Dashboard view bound to a <see cref="DashboardViewModel"/>.</returns>
    [Authorize]
    public async Task<IActionResult> Dashboard()
    {
        var uid          = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var applications = await _context.JobApplications
            .Where(a => a.UserId == uid)
            .ToListAsync();

        var profile   = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == uid);
        var hasResume = await _context.ResumeVersions.AnyAsync(r => r.UserId == uid);

        var today  = DateTime.UtcNow.Date;
        int offers = applications.Count(a => a.Status == ApplicationStatus.Offer);
        int total  = applications.Count;

        var topCompanies = applications
            .GroupBy(a => a.CompanyName)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .ToList();

        var upcomingDeadlines = applications
            .Where(a => a.Deadline.HasValue
                     && a.Deadline.Value.Date >= today
                     && a.Deadline.Value.Date <= today.AddDays(3)
                     && a.Status != ApplicationStatus.Rejected)
            .OrderBy(a => a.Deadline)
            .ToList();

        var followUpSuggestions = applications
            .Where(a => a.Status == ApplicationStatus.Applied
                     && a.DateApplied.HasValue
                     && (today - a.DateApplied.Value.Date).Days >= 7)
            .OrderBy(a => a.DateApplied)
            .Take(5)
            .ToList();

        var monthStarts = Enumerable.Range(0, 6)
            .Select(i => new DateTime(today.Year, today.Month, 1).AddMonths(-(5 - i)))
            .ToList();
        var applicationsOverTime = monthStarts
            .Select(m => new KeyValuePair<string, int>(
                m.ToString("MMM"),
                applications.Count(a => a.DateApplied.HasValue
                    && a.DateApplied.Value.Year == m.Year
                    && a.DateApplied.Value.Month == m.Month)))
            .ToList();

        var vm = new DashboardViewModel
        {
            TotalApplications = total,
            StatusCounts = Enum.GetValues<ApplicationStatus>()
                .ToDictionary(s => s, s => applications.Count(a => a.Status == s)),
            RecentApplications = applications
                .OrderByDescending(a => a.Id)
                .Take(8)
                .ToList(),
            SuccessRate          = total > 0 ? Math.Round(offers * 100.0 / total, 1) : 0,
            TopCompanies         = topCompanies,
            ApplicationsOverTime = applicationsOverTime,
            UpcomingDeadlines    = upcomingDeadlines,
            FollowUpSuggestions  = followUpSuggestions,
            HasProfileBasics     = profile != null && !string.IsNullOrWhiteSpace(profile.FullName)
                                    && !string.IsNullOrWhiteSpace(profile.SkillsJson) && profile.SkillsJson != "[]",
            HasResume            = hasResume
        };

        return View(vm);
    }

    /// <summary>Renders the static privacy policy page.</summary>
    public IActionResult Privacy()
    {
        return View();
    }

    /// <summary>
    /// Custom 404 page, wired up as the status-code-pages re-execute target for unmatched routes
    /// (and for actions that return a 404, e.g. antiforgery failures). Replaces the default ASP.NET
    /// error page with one matching the site's design.
    /// </summary>
    [Route("Home/NotFound")]
    public IActionResult PageNotFound()
    {
        Response.StatusCode = 404;
        return View("NotFound");
    }

    /// <summary>
    /// Custom 500 page shown for unhandled exceptions. Caching is explicitly disabled so a stale
    /// error page is never served from a browser/proxy cache after the underlying issue is fixed.
    /// </summary>
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        Response.StatusCode = 500;
        return View();
    }
}
