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

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _context;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public IActionResult Index()
    {
        return View();
    }

    [Authorize]
    public async Task<IActionResult> Dashboard()
    {
        var uid          = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var applications = await _context.JobApplications
            .Where(a => a.UserId == uid)
            .ToListAsync();

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

        var vm = new DashboardViewModel
        {
            TotalApplications = total,
            StatusCounts = Enum.GetValues<ApplicationStatus>()
                .ToDictionary(s => s, s => applications.Count(a => a.Status == s)),
            RecentApplications = applications
                .OrderByDescending(a => a.Id)
                .Take(8)
                .ToList(),
            SuccessRate        = total > 0 ? Math.Round(offers * 100.0 / total, 1) : 0,
            TopCompanies       = topCompanies,
            UpcomingDeadlines  = upcomingDeadlines,
            FollowUpSuggestions = followUpSuggestions
        };

        return View(vm);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [Route("Home/NotFound")]
    public IActionResult PageNotFound()
    {
        Response.StatusCode = 404;
        return View("NotFound");
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        Response.StatusCode = 500;
        return View();
    }
}
