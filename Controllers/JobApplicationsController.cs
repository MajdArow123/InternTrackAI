using System.Security.Claims;
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

    // ── Create ────────────────────────────────────────────────

    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(JobApplication jobApplication)
    {
        jobApplication.UserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "guest";
        ModelState.Remove(nameof(jobApplication.UserId));

        if (!ModelState.IsValid)
            return View(jobApplication);

        _context.Add(jobApplication);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Edit ──────────────────────────────────────────────────

    public async Task<IActionResult> Edit(int id)
    {
        var app = await _context.JobApplications.FindAsync(id);
        if (app is null) return NotFound();
        return View(app);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, JobApplication jobApplication)
    {
        if (id != jobApplication.Id) return BadRequest();

        jobApplication.UserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "guest";
        ModelState.Remove(nameof(jobApplication.UserId));

        if (!ModelState.IsValid)
            return View(jobApplication);

        try
        {
            _context.Update(jobApplication);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.JobApplications.AnyAsync(e => e.Id == id))
                return NotFound();
            throw;
        }

        return RedirectToAction(nameof(Index));
    }

    // ── Delete ────────────────────────────────────────────────

    public async Task<IActionResult> Delete(int id)
    {
        var app = await _context.JobApplications.FindAsync(id);
        if (app is null) return NotFound();
        return View(app);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var app = await _context.JobApplications.FindAsync(id);
        if (app is not null)
        {
            _context.JobApplications.Remove(app);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }
}
