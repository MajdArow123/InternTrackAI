using System.Security.Claims;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

[Authorize]
public class JobApplicationsController : Controller
{
    private readonly ApplicationDbContext _context;

    public JobApplicationsController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var jobApplications = await _context.JobApplications
            .Where(j => j.UserId == userId)
            .ToListAsync();

        return View(jobApplications);
    }

    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(JobApplication jobApplication)
    {
        if (!ModelState.IsValid)
        {
            return View(jobApplication);
        }

        jobApplication.UserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        _context.Add(jobApplication);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}