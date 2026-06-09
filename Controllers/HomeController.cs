using System.Diagnostics;
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

    public async Task<IActionResult> Dashboard()
    {
        var applications = await _context.JobApplications.ToListAsync();

        var vm = new DashboardViewModel
        {
            TotalApplications = applications.Count,
            StatusCounts = Enum.GetValues<ApplicationStatus>()
                .ToDictionary(s => s, s => applications.Count(a => a.Status == s)),
            RecentApplications = applications
                .OrderByDescending(a => a.Id)
                .Take(8)
                .ToList()
        };

        return View(vm);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
