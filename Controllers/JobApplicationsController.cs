using System.Security.Claims;
using System.Text;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Controllers;

/// <summary>
/// Core CRUD controller for the user's tracked internship applications: listing with
/// search/filter/sort, create, edit, delete (single and bulk), bulk status changes, and CSV export.
/// Every action scopes its query to the signed-in user's <see cref="JobApplication.UserId"/>.
/// </summary>
[Authorize]
public class JobApplicationsController : Controller
{
    private readonly ApplicationDbContext _context;

    public JobApplicationsController(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>Resolves the current signed-in user's id from the auth claims (ASP.NET Identity's "sub" claim).</summary>
    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── Index (search / filter / sort) ────────────────────

    /// <summary>
    /// Lists the current user's applications, optionally filtered by company/role text search,
    /// status, and work mode, and sorted by deadline, date applied, status, or company name
    /// (defaults to newest-first by id).
    /// </summary>
    /// <param name="search">Case-sensitive substring match against company name or role title.</param>
    /// <param name="status">String name of an <see cref="ApplicationStatus"/> value; ignored if it doesn't parse.</param>
    /// <param name="workMode">String name of a <see cref="WorkMode"/> value; ignored if it doesn't parse.</param>
    /// <param name="sortBy">One of "deadline", "dateApplied", "status", "company"; any other value falls back to id descending.</param>
    /// <returns>The Index view with the filtered/sorted list, plus filter state and total count in ViewBag.</returns>
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

    /// <summary>Renders the empty Add Application form, including the AI Job Analyzer panel.</summary>
    public IActionResult Create()
    {
        return View();
    }

    /// <summary>
    /// Saves a new application for the current user. The posted <c>UserId</c> is ignored and
    /// overwritten with the authenticated user's id (so a client can't submit on another user's
    /// behalf), then removed from <see cref="ModelState"/> so it doesn't fail required-field validation.
    /// Unless <paramref name="forceCreate"/> is set, a matching company+role pair for this user
    /// re-renders the form with a duplicate warning instead of saving.
    /// </summary>
    /// <param name="jobApplication">Form-bound application fields, including any AI-analyzer hidden inputs (match score/skills/summary).</param>
    /// <param name="forceCreate">When true, bypasses the duplicate-application check (set by the "Save Anyway" button).</param>
    /// <returns>Redirect to Index on success; otherwise re-renders Create with validation or duplicate-warning state.</returns>
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

    /// <summary>Loads an application by id for editing. Returns 404 if it doesn't exist.</summary>
    public async Task<IActionResult> Edit(int id)
    {
        var app = await _context.JobApplications.FindAsync(id);
        if (app is null) return NotFound();
        return View(app);
    }

    /// <summary>
    /// Persists edits to an existing application. Re-stamps <c>UserId</c> from the authenticated
    /// user (same rationale as Create) and handles the case where the row was deleted concurrently.
    /// </summary>
    /// <param name="id">Route id; must match <paramref name="jobApplication"/>'s id or the request is rejected.</param>
    /// <param name="jobApplication">Form-bound updated fields.</param>
    /// <returns>Redirect to Index on success; 400 on id mismatch; 404 if the row no longer exists; otherwise re-renders Edit.</returns>
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

    /// <summary>Loads an application by id for the delete confirmation page. Returns 404 if it doesn't exist.</summary>
    public async Task<IActionResult> Delete(int id)
    {
        var app = await _context.JobApplications.FindAsync(id);
        if (app is null) return NotFound();
        return View(app);
    }

    /// <summary>
    /// Deletes an application along with any cover letters and interview prep sessions linked to
    /// it, so no orphaned child rows are left behind (there's no DB-level cascade configured for
    /// these relations). Named "Delete" via <see cref="ActionNameAttribute"/> so the POST shares
    /// the GET confirmation page's route.
    /// </summary>
    /// <param name="id">The application id to delete.</param>
    /// <returns>Redirect to Index, with a toast confirming the deletion (silently no-ops if the id doesn't exist).</returns>
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

    /// <summary>
    /// Deletes multiple applications at once (the Applications list's checkbox + bulk-action bar),
    /// scoped to the current user, cleaning up linked cover letters and interview prep sessions
    /// the same way single delete does.
    /// </summary>
    /// <param name="ids">Application ids to delete; ids not owned by the current user are silently ignored.</param>
    /// <returns>Redirect to Index with a toast reporting how many were deleted.</returns>
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

    /// <summary>Sets the same status on multiple applications at once, scoped to the current user.</summary>
    /// <param name="ids">Application ids to update; ids not owned by the current user are silently ignored.</param>
    /// <param name="newStatus">The status to apply to all selected applications.</param>
    /// <returns>Redirect to Index with a toast reporting how many were updated.</returns>
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

    /// <summary>Exports all of the current user's applications as a downloadable CSV file.</summary>
    /// <returns>A "text/csv" file response named "applications.csv".</returns>
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

    /// <summary>Quotes and escapes a CSV field if it contains a comma, quote, or newline.</summary>
    private static string Csv(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}
