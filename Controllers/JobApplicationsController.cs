using System.Security.Claims;
using System.Text;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
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

    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── Index (search / filter / sort) ────────────────────

    public async Task<IActionResult> Index(
        string? search, string? status, string? workMode, string? sortBy)
    {
        var uid   = UserId();
        var query = _context.JobApplications.Where(a => a.UserId == uid);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a =>
                a.CompanyName.Contains(search) || a.RoleTitle.Contains(search));

        if (Enum.TryParse<ApplicationStatus>(status, out var s))
            query = query.Where(a => a.Status == s);

        if (Enum.TryParse<WorkMode>(workMode, out var wm))
            query = query.Where(a => a.WorkMode == wm);

        query = sortBy switch
        {
            "deadline"    => query.OrderBy(a => a.Deadline),
            "dateApplied" => query.OrderByDescending(a => a.DateApplied),
            "status"      => query.OrderBy(a => (int)a.Status),
            "company"     => query.OrderBy(a => a.CompanyName),
            _             => query.OrderByDescending(a => a.Id)
        };

        var apps       = await query.ToListAsync();
        var totalCount = await _context.JobApplications.CountAsync(a => a.UserId == uid);

        ViewBag.Search     = search;
        ViewBag.Status     = status;
        ViewBag.WorkMode   = workMode;
        ViewBag.SortBy     = sortBy;
        ViewBag.IsFiltered = !string.IsNullOrWhiteSpace(search)
                          || !string.IsNullOrWhiteSpace(status)
                          || !string.IsNullOrWhiteSpace(workMode);
        ViewBag.TotalCount = totalCount;

        return View(apps);
    }

    // ── Create ────────────────────────────────────────────

    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(JobApplication jobApplication, bool forceCreate = false)
    {
        var uid = UserId();
        jobApplication.UserId = uid;
        ModelState.Remove(nameof(jobApplication.UserId));

        if (!ModelState.IsValid)
            return View(jobApplication);

        if (!forceCreate)
        {
            var isDuplicate = await _context.JobApplications.AnyAsync(a =>
                a.UserId == uid &&
                a.CompanyName == jobApplication.CompanyName &&
                a.RoleTitle   == jobApplication.RoleTitle);

            if (isDuplicate)
            {
                ViewBag.DuplicateWarning = true;
                return View(jobApplication);
            }
        }

        _context.Add(jobApplication);
        await _context.SaveChangesAsync();
        TempData["Toast"] = "success|Application saved successfully.";
        return RedirectToAction(nameof(Index));
    }

    // ── Edit ──────────────────────────────────────────────

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

        jobApplication.UserId = UserId();
        ModelState.Remove(nameof(jobApplication.UserId));

        if (!ModelState.IsValid)
            return View(jobApplication);

        try
        {
            _context.Update(jobApplication);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "success|Application updated.";
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.JobApplications.AnyAsync(e => e.Id == id))
                return NotFound();
            throw;
        }

        return RedirectToAction(nameof(Index));
    }

    // ── Delete ────────────────────────────────────────────

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
            var linkedLetters = await _context.GeneratedCoverLetters
                .Where(c => c.JobApplicationId == id).ToListAsync();
            _context.GeneratedCoverLetters.RemoveRange(linkedLetters);

            var linkedPreps = await _context.InterviewPrepSessions
                .Where(s => s.JobApplicationId == id).ToListAsync();
            _context.InterviewPrepSessions.RemoveRange(linkedPreps);

            await _context.SaveChangesAsync();

            _context.JobApplications.Remove(app);
            await _context.SaveChangesAsync();
            TempData["Toast"] = $"success|{app.CompanyName} — {app.RoleTitle} deleted.";
        }
        return RedirectToAction(nameof(Index));
    }

    // ── Bulk Delete ───────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDelete(int[] ids)
    {
        if (ids.Length == 0) return RedirectToAction(nameof(Index));

        var uid  = UserId();
        var apps = await _context.JobApplications
            .Where(a => ids.Contains(a.Id) && a.UserId == uid)
            .ToListAsync();

        if (apps.Count > 0)
        {
            var appIds = apps.Select(a => a.Id).ToArray();

            var linkedLetters = await _context.GeneratedCoverLetters
                .Where(c => c.JobApplicationId.HasValue && appIds.Contains(c.JobApplicationId.Value))
                .ToListAsync();
            _context.GeneratedCoverLetters.RemoveRange(linkedLetters);

            var linkedPreps = await _context.InterviewPrepSessions
                .Where(s => appIds.Contains(s.JobApplicationId))
                .ToListAsync();
            _context.InterviewPrepSessions.RemoveRange(linkedPreps);

            await _context.SaveChangesAsync();

            _context.JobApplications.RemoveRange(apps);
            await _context.SaveChangesAsync();

            TempData["Toast"] = $"success|{apps.Count} application{(apps.Count == 1 ? "" : "s")} deleted.";
        }

        return RedirectToAction(nameof(Index));
    }

    // ── Bulk Status Update ────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkStatus(int[] ids, ApplicationStatus newStatus)
    {
        if (ids.Length == 0) return RedirectToAction(nameof(Index));

        var uid  = UserId();
        var apps = await _context.JobApplications
            .Where(a => ids.Contains(a.Id) && a.UserId == uid)
            .ToListAsync();

        foreach (var app in apps)
            app.Status = newStatus;

        await _context.SaveChangesAsync();
        TempData["Toast"] = $"success|{apps.Count} application{(apps.Count == 1 ? "" : "s")} set to {newStatus}.";

        return RedirectToAction(nameof(Index));
    }

    // ── Export CSV ────────────────────────────────────────

    public async Task<IActionResult> Export()
    {
        var uid  = UserId();
        var apps = await _context.JobApplications
            .Where(a => a.UserId == uid)
            .OrderByDescending(a => a.Id)
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Company,Role,Location,Work Mode,Status,Deadline,Date Applied,Salary,Job Link");

        foreach (var app in apps)
        {
            sb.AppendLine(string.Join(",",
                Csv(app.CompanyName),
                Csv(app.RoleTitle),
                Csv(app.Location ?? ""),
                Csv(app.WorkMode.ToString()),
                Csv(app.Status.ToString()),
                Csv(app.Deadline?.ToString("yyyy-MM-dd") ?? ""),
                Csv(app.DateApplied?.ToString("yyyy-MM-dd") ?? ""),
                Csv(app.Salary ?? ""),
                Csv(app.JobLink ?? "")));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", "applications.csv");
    }

    private static string Csv(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}
