using System.Security.Claims;
using System.Text;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
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

    /// <summary>Loads one of the current user's applications for editing. 404 if it doesn't exist or isn't theirs.</summary>
    public async Task<IActionResult> Edit(int id)
    {
        var app = await FindOwnedAsync(id);
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

        // The row must already exist and belong to this user; otherwise a client could post an
        // arbitrary id and overwrite (and take ownership of) someone else's application.
        var uid = UserId();
        if (!await _context.JobApplications.AnyAsync(a => a.Id == id && a.UserId == uid))
            return NotFound();

        jobApplication.UserId = uid;
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

    /// <summary>Loads one of the current user's applications for the delete confirmation page. 404 if it isn't theirs.</summary>
    public async Task<IActionResult> Delete(int id)
    {
        var app = await FindOwnedAsync(id);
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
    /// <returns>Redirect to Index with a toast confirming the deletion; 404 if the id isn't one of the user's applications.</returns>
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var uid = UserId();
        var app = await FindOwnedAsync(id);
        if (app is null) return NotFound();

        var linkedLetters = await _context.GeneratedCoverLetters
            .Where(c => c.JobApplicationId == id && c.UserId == uid).ToListAsync();
        _context.GeneratedCoverLetters.RemoveRange(linkedLetters);

        var linkedPreps = await _context.InterviewPrepSessions
            .Where(s => s.JobApplicationId == id && s.UserId == uid).ToListAsync();
        _context.InterviewPrepSessions.RemoveRange(linkedPreps);

        await _context.SaveChangesAsync();

        _context.JobApplications.Remove(app);
        await _context.SaveChangesAsync();
        TempData["Toast"] = $"success|{app.CompanyName} — {app.RoleTitle} deleted.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Loads an application only if it belongs to the signed-in user; null otherwise.</summary>
    private Task<JobApplication?> FindOwnedAsync(int id)
    {
        var uid = UserId();
        return _context.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.UserId == uid);
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
                .Where(c => c.UserId == uid && c.JobApplicationId.HasValue && appIds.Contains(c.JobApplicationId.Value))
                .ToListAsync();
            _context.GeneratedCoverLetters.RemoveRange(linkedLetters);

            var linkedPreps = await _context.InterviewPrepSessions
                .Where(s => s.UserId == uid && appIds.Contains(s.JobApplicationId))
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

    // ── Kanban board: move / reorder ──────────────────────

    /// <summary>
    /// Moves one application to a status column and position (Kanban drag/drop or keyboard move).
    /// Body: <c>{ status, boardOrder }</c>. 404 when the application isn't the current user's,
    /// 400 when the status isn't a real <see cref="ApplicationStatus"/> value.
    /// </summary>
    /// <returns>200 <c>{ id, status, boardOrder }</c>.</returns>
    [HttpPost("JobApplications/{id:int}/move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(int id, [FromBody] MoveRequest? req)
    {
        if (req is null)
            return BadRequest(new { success = false, error = "Request body is required." });
        if (!TryParseStatus(req.Status, out var status))
            return BadRequest(new { success = false, error = $"'{req.Status}' is not a valid status." });

        var app = await FindOwnedAsync(id);
        if (app is null)
            return NotFound(new { success = false, error = "Application not found." });

        app.Status     = status;
        app.BoardOrder = Math.Max(0, req.BoardOrder);
        await _context.SaveChangesAsync();

        return Ok(new { id = app.Id, status = app.Status.ToString(), boardOrder = app.BoardOrder });
    }

    /// <summary>
    /// Persists the card order of one status column: <c>BoardOrder = index</c> for each id in
    /// <c>ids</c>, restricted to the current user's applications that currently have that status
    /// (other ids are ignored). Runs in a single transaction.
    /// </summary>
    /// <returns>200 <c>{ status, updated }</c>; 400 for an unknown status or missing ids.</returns>
    [HttpPost("JobApplications/reorder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest? req)
    {
        if (req is null || req.Ids is null)
            return BadRequest(new { success = false, error = "Request body with ids is required." });
        if (!TryParseStatus(req.Status, out var status))
            return BadRequest(new { success = false, error = $"'{req.Status}' is not a valid status." });

        var uid  = UserId();
        var ids  = req.Ids.Distinct().ToArray();
        var apps = await _context.JobApplications
            .Where(a => a.UserId == uid && a.Status == status && ids.Contains(a.Id))
            .ToListAsync();

        await using var tx = await _context.Database.BeginTransactionAsync();
        foreach (var app in apps)
            app.BoardOrder = Array.IndexOf(ids, app.Id);
        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        return Ok(new { status = status.ToString(), updated = apps.Count });
    }

    /// <summary>Parses a status name (case-insensitive); rejects blanks and bare numbers like "7".</summary>
    private static bool TryParseStatus(string? raw, out ApplicationStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(raw) || char.IsDigit(raw.Trim()[0])) return false;
        return Enum.TryParse(raw.Trim(), ignoreCase: true, out status) && Enum.IsDefined(status);
    }

    // ── Notes / activity timeline (drawer) ────────────────

    /// <summary>Returns the note timeline for an application, newest first, as JSON for the detail drawer.</summary>
    [HttpGet]
    public async Task<IActionResult> Notes(int appId)
    {
        var uid = UserId();
        var owns = await _context.JobApplications
            .AnyAsync(a => a.Id == appId && a.UserId == uid);
        if (!owns) return NotFound();

        var notes = await _context.ApplicationNotes
            .Where(n => n.JobApplicationId == appId && n.UserId == uid)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new { n.Id, n.Text, createdAt = n.CreatedAt.ToString("MMM d, yyyy 'at' h:mm tt") })
            .ToListAsync();

        return Json(notes);
    }

    /// <summary>Appends a new note to an application's activity timeline.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(int appId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return BadRequest();

        var uid = UserId();
        var owns = await _context.JobApplications
            .AnyAsync(a => a.Id == appId && a.UserId == uid);
        if (!owns) return NotFound();

        var note = new ApplicationNote
        {
            JobApplicationId = appId,
            UserId = uid,
            Text = text.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _context.ApplicationNotes.Add(note);
        await _context.SaveChangesAsync();

        return Json(new { note.Id, note.Text, createdAt = note.CreatedAt.ToString("MMM d, yyyy 'at' h:mm tt") });
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

    // ── Import CSV ────────────────────────────────────────

    /// <summary>
    /// Bulk-imports applications from a CSV file using the same column layout produced by
    /// <see cref="Export"/> (Company,Role,Location,Work Mode,Status,Deadline,Date Applied,Salary,Job Link).
    /// Parsing lives in <see cref="CsvImportParser"/>: rows missing a Company or Role are skipped,
    /// duplicates (in the file or already tracked) are dropped, and Work Mode / Status fall back to
    /// Remote / Saved when blank or unrecognized.
    /// </summary>
    /// <param name="file">The uploaded .csv file.</param>
    /// <returns>Redirect to Index with a toast reporting how many rows were imported (and skipped, if any).</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCsv(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Toast"] = "error|Choose a CSV file to import.";
            return RedirectToAction(nameof(Index));
        }

        var uid = UserId();

        // Existing company+role pairs so re-importing an export doesn't duplicate rows.
        var existing = await _context.JobApplications
            .Where(a => a.UserId == uid)
            .Select(a => new { a.CompanyName, a.RoleTitle })
            .ToListAsync();

        using var reader = new StreamReader(file.OpenReadStream());
        var result = CsvImportParser.Parse(reader, uid, existing.Select(e => (e.CompanyName, e.RoleTitle)));

        if (result.Applications.Count > 0)
        {
            _context.JobApplications.AddRange(result.Applications);
            await _context.SaveChangesAsync();
        }

        var n = result.Applications.Count;
        var msg = $"Imported {n} application{(n == 1 ? "" : "s")}";
        if (result.Skipped > 0)    msg += $", skipped {result.Skipped} invalid row{(result.Skipped == 1 ? "" : "s")}";
        if (result.Duplicates > 0) msg += $", skipped {result.Duplicates} duplicate{(result.Duplicates == 1 ? "" : "s")}";
        TempData["Toast"] = "success|" + msg + ".";

        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Body of <see cref="JobApplicationsController.Move"/>.</summary>
public record MoveRequest(string? Status, int BoardOrder);

/// <summary>Body of <see cref="JobApplicationsController.Reorder"/>.</summary>
public record ReorderRequest(string? Status, int[]? Ids);
