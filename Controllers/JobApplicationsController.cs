using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

public class JobApplicationsController : Controller
{
    private readonly ApplicationDbContext _context;

    public JobApplicationsController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _context.JobApplications.ToListAsync());
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

        _context.Add(jobApplication);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}