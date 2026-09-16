using System.Security.Claims;
using System.Text;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using InternTrackAI.Services.Gmail;
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
    private readonly ReminderService _reminders;
    private readonly UserClockProvider _clocks;
    private readonly SuggestionService _suggestions;
    private readonly KeywordCoverageService _keywords;

    public JobApplicationsController(ApplicationDbContext context, ReminderService reminders, UserClockProvider clocks,
                                     SuggestionService suggestions, KeywordCoverageService keywords)
    {
        _context     = context;
        _reminders   = reminders;
        _clocks      = clocks;
        _suggestions = suggestions;
        _keywords    = keywords;
    }

    /// <summary>
    /// The Create/Edit forms read and write InterviewAt / FollowUpAt as the user's wall-clock time;
    /// storage is UTC. Deadline and DateApplied are calendar dates and are left alone.
    /// </summary>
    private static void FormToUtc(JobApplication app, UserClock clock)
    {
        app.InterviewAt = clock.ToUtc(app.InterviewAt);
        app.FollowUpAt  = clock.ToUtc(app.FollowUpAt);
    }

    private static void UtcToForm(JobApplication app, UserClock clock)
    {
        app.InterviewAt = clock.ToLocal(app.InterviewAt);
        app.FollowUpAt  = clock.ToLocal(app.FollowUpAt);
    }

    /// <summary>Resolves the current signed-in user's id from the auth claims (ASP.NET Identity's "sub" claim).</summary>
    private string UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── Index (search / filter / sort) ────────────────────

    /// <summary>Cookie remembering whether the user last used the list or the board view.</summary>
    public const string ViewCookie = "apps_view";

    /// <summary>
    /// Lists the current user's applications, optionally filtered by company/role text search,
    /// status, and work mode, and sorted by deadline, date applied, status, or company name
    /// (defaults to newest-first by id). When the user last used the board view (see
    /// <see cref="ViewCookie"/>) and no explicit <paramref name="view"/> was requested, redirects
    /// to <see cref="Board"/> with the same filters.
    /// </summary>
    /// <param name="search">Case-sensitive substring match against company name or role title.</param>
    /// <param name="status">String name of an <see cref="ApplicationStatus"/> value; ignored if it doesn't parse.</param>
    /// <param name="workMode">String name of a <see cref="WorkMode"/> value; ignored if it doesn't parse.</param>
    /// <param name="sortBy">One of "deadline", "dateApplied", "status", "company"; any other value falls back to id descending.</param>
    /// <param name="view">"list" forces the list even if the cookie says board (used by the view toggle).</param>
    /// <param name="attention">When true, only applications that <see cref="ReminderService"/> flags are shown (the "Needs attention" pill).</param>
    /// <returns>The Index view with the filtered/sorted list, plus filter state and total count in ViewBag.</returns>
    public async Task<IActionResult> Index(
        string? search, string? status, string? workMode, string? sortBy, string? view, bool attention = false)
    {
        if (view is null && Request.Cookies[ViewCookie] == "board")
            return RedirectToAction(nameof(Board), new { search, status, workMode, sortBy, attention = attention ? "true" : null });

        RememberView("list");

        var uid  = UserId();
        var apps = await FilteredQuery(uid, search, status, workMode, sortBy).ToListAsync();
        apps = await ApplyAttentionAsync(uid, apps, attention);
        await SetFilterViewBagAsync(uid, search, status, workMode, sortBy, attention);
        ViewBag.ResumeLabels = await ResumeLabelsAsync(uid);

        return View(apps);
    }

    // ── Board (Kanban) ────────────────────────────────────

    /// <summary>
    /// Kanban view of the same filtered set as <see cref="Index"/>: one column per status in
    /// pipeline order, cards ordered by <see cref="JobApplication.BoardOrder"/> then newest
    /// applied first. Remembers the choice in <see cref="ViewCookie"/>.
    /// </summary>
    public async Task<IActionResult> Board(string? search, string? status, string? workMode, string? sortBy, bool attention = false)
    {
        RememberView("board");

        var uid  = UserId();
        var apps = await FilteredQuery(uid, search, status, workMode, sortBy).ToListAsync();
        apps = await ApplyAttentionAsync(uid, apps, attention);
        await SetFilterViewBagAsync(uid, search, status, workMode, sortBy, attention);
        ViewBag.ResumeLabels = await ResumeLabelsAsync(uid);

        var ordered = apps
            .OrderBy(a => a.BoardOrder)
            .ThenByDescending(a => a.DateApplied ?? DateTime.MinValue)
            .ThenByDescending(a => a.Id)
            .ToList();

        return View(ordered);
    }

    /// <summary>Statuses in pipeline order, shared by the filter pills and the board columns.</summary>
    public static readonly ApplicationStatus[] PipelineOrder =
    {
        ApplicationStatus.Saved, ApplicationStatus.Applied, ApplicationStatus.Interview,
        ApplicationStatus.Offer, ApplicationStatus.Rejected
    };

    /// <summary>The user-scoped, filtered, sorted query behind both the list and the board.</summary>
    private IQueryable<JobApplication> FilteredQuery(string uid, string? search, string? status, string? workMode, string? sortBy)
    {
        var query = _context.JobApplications.Where(a => a.UserId == uid);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a =>
                a.CompanyName.Contains(search) || a.RoleTitle.Contains(search));

        if (Enum.TryParse<ApplicationStatus>(status, out var s))
            query = query.Where(a => a.Status == s);

        if (Enum.TryParse<WorkMode>(workMode, out var wm))
            query = query.Where(a => a.WorkMode == wm);

        return sortBy switch
        {
            "deadline"    => query.OrderBy(a => a.Deadline),
            "dateApplied" => query.OrderByDescending(a => a.DateApplied),
            "status"      => query.OrderBy(a => (int)a.Status),
            "company"     => query.OrderBy(a => a.CompanyName),
            _             => query.OrderByDescending(a => a.Id)
        };
    }

    /// <summary>
    /// Computes the user's reminders once per page and exposes them to the row/card partials as
    /// <c>ViewBag.AttentionById</c> (chips, drawer values) plus the "Needs attention" pill count.
    /// When <paramref name="attention"/> is set, narrows the page to flagged applications.
    /// </summary>
    private async Task<List<JobApplication>> ApplyAttentionAsync(string uid, List<JobApplication> apps, bool attention)
    {
        var byId = (await _reminders.ForUserAsync(uid))
            .GroupBy(r => r.Application.Id)
            .ToDictionary(g => g.Key, g => g.ToList());

        ViewBag.AttentionById  = byId;
        ViewBag.AttentionCount = byId.Count;
        ViewBag.Attention      = attention;
        // Pending inbox suggestions per application: the drawer section, the list/board dot indicators.
        ViewBag.SuggestionsById = await _suggestions.PendingByApplicationAsync(uid);

        return attention ? apps.Where(a => byId.ContainsKey(a.Id)).ToList() : apps;
    }

    private async Task SetFilterViewBagAsync(string uid, string? search, string? status, string? workMode, string? sortBy, bool attention)
    {
        ViewBag.Search     = search;
        ViewBag.Status     = status;
        ViewBag.WorkMode   = workMode;
        ViewBag.SortBy     = sortBy;
        ViewBag.IsFiltered = !string.IsNullOrWhiteSpace(search)
                          || !string.IsNullOrWhiteSpace(status)
                          || !string.IsNullOrWhiteSpace(workMode)
                          || attention;
        ViewBag.TotalCount = await _context.JobApplications.CountAsync(a => a.UserId == uid);
    }

    private void RememberView(string view) =>
        Response.Cookies.Append(ViewCookie, view, new CookieOptions
        {
            Expires     = DateTimeOffset.UtcNow.AddYears(1),
            HttpOnly    = true,
            SameSite    = SameSiteMode.Lax,
            IsEssential = true
        });

    // ── Create ────────────────────────────────────────────

    /// <summary>
    /// Renders the Add Application form, including the AI Job Analyzer panel. The form starts
    /// empty unless the caller pre-fills it: the board's per-column "+" button passes a
    /// <paramref name="status"/>, and the bookmarklet's <c>/Capture</c> endpoint passes the
    /// extracted fields as query parameters bound into <paramref name="prefill"/>. Either way the
    /// user reviews and submits the form themselves — nothing is saved on GET.
    /// </summary>
    /// <param name="status">Optional status to pre-select (the board's per-column "+" button).</param>
    /// <param name="prefill">Optional bookmarklet capture fields (url, title, company, role, location, salary, deadline, workMode).</param>
    public async Task<IActionResult> Create(ApplicationStatus? status, [FromQuery] CapturePrefill? prefill)
    {
        var app = new JobApplication { Status = status ?? default };
        if (prefill is { HasAny: true })
        {
            prefill.ApplyTo(app);
            ViewBag.CapturedFrom = prefill.Host;
        }

        // "Resume used" starts on the active resume (the one AI matching used); the user can override.
        var resumes = await SetResumeOptionsAsync(UserId());
        app.ResumeVersionId = resumes.FirstOrDefault(r => r.IsActive)?.Id;
        return View(app);
    }

    /// <summary>
    /// Saves a new application for the current user. The posted <c>UserId</c> is ignored and
    /// overwritten with the authenticated user's id (so a client can't submit on another user's
    /// behalf), then removed from <see cref="ModelState"/> so it doesn't fail required-field validation.
    /// Unless <paramref name="forceCreate"/> is set, a matching company+role pair for this user
    /// (compared trimmed and case-insensitively, see <see cref="IsDuplicateAsync"/>) re-renders the
    /// form with a duplicate warning instead of saving.
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
        var resumes = await SetResumeOptionsAsync(uid);
        KeepOwnedResume(jobApplication, resumes);

        if (!ModelState.IsValid)
            return View(jobApplication);

        TrimNames(jobApplication);
        if (!forceCreate && await IsDuplicateAsync(uid, jobApplication))
        {
            ViewBag.DuplicateWarning = true;
            return View(jobApplication);
        }

        FormToUtc(jobApplication, await _clocks.GetAsync());
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
        UtcToForm(app, await _clocks.GetAsync());   // the entity is not saved on GET, so this only affects the form
        await SetResumeOptionsAsync(UserId());
        return View(app);
    }

    /// <summary>
    /// Persists edits to an existing application. Re-stamps <c>UserId</c> from the authenticated
    /// user (same rationale as Create) and handles the case where the row was deleted concurrently.
    /// Renaming an application onto another one's company+role (trimmed, case-insensitive) shows
    /// the same duplicate warning as Create unless <paramref name="forceSave"/> is set.
    /// </summary>
    /// <param name="id">Route id; must match <paramref name="jobApplication"/>'s id or the request is rejected.</param>
    /// <param name="jobApplication">Form-bound updated fields.</param>
    /// <param name="forceSave">When true, bypasses the duplicate check (set by the "Save Anyway" button).</param>
    /// <returns>Redirect to Index on success; 400 on id mismatch; 404 if the row no longer exists; otherwise re-renders Edit.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, JobApplication jobApplication, bool forceSave = false)
    {
        if (id != jobApplication.Id) return BadRequest();

        // The row must already exist and belong to this user; otherwise a client could post an
        // arbitrary id and overwrite (and take ownership of) someone else's application.
        var uid = UserId();
        if (!await _context.JobApplications.AnyAsync(a => a.Id == id && a.UserId == uid))
            return NotFound();

        jobApplication.UserId = uid;
        ModelState.Remove(nameof(jobApplication.UserId));
        KeepOwnedResume(jobApplication, await SetResumeOptionsAsync(uid));

        if (!ModelState.IsValid)
            return View(jobApplication);

        TrimNames(jobApplication);
        if (!forceSave && await IsDuplicateAsync(uid, jobApplication, excludeId: id))
        {
            ViewBag.DuplicateWarning = true;
            return View(jobApplication);
        }

        FormToUtc(jobApplication, await _clocks.GetAsync());
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
    /// <summary>
    /// True when the user already tracks another application with the same company and role.
    /// Both sides are compared trimmed and case-insensitively (SQL <c>lower(trim(...))</c> on either
    /// provider), so " stripe " / "Stripe" and "intern" / "Intern" count as the same pair.
    /// </summary>
    private async Task<bool> IsDuplicateAsync(string uid, JobApplication app, int? excludeId = null)
    {
        var company = app.CompanyName.Trim().ToLowerInvariant();
        var role    = app.RoleTitle.Trim().ToLowerInvariant();
        return await _context.JobApplications.AnyAsync(a =>
            a.UserId == uid &&
            (excludeId == null || a.Id != excludeId) &&
            a.CompanyName.Trim().ToLower() == company &&
            a.RoleTitle.Trim().ToLower()   == role);
    }

    /// <summary>Stores company and role without surrounding whitespace so lists, filters, and the duplicate check line up.</summary>
    private static void TrimNames(JobApplication app)
    {
        app.CompanyName = app.CompanyName.Trim();
        app.RoleTitle   = app.RoleTitle.Trim();
    }

    private Task<JobApplication?> FindOwnedAsync(int id)
    {
        var uid = UserId();
        return _context.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.UserId == uid);
    }

    // ── Resume used ───────────────────────────────────────

    /// <summary>The user's resume versions, newest first, exposed to the Create/Edit forms as <c>ViewBag.ResumeOptions</c>.</summary>
    private async Task<List<ResumeVersion>> SetResumeOptionsAsync(string uid)
    {
        var resumes = await _context.ResumeVersions.AsNoTracking()
            .Where(r => r.UserId == uid)
            .OrderByDescending(r => r.VersionNumber)
            .ToListAsync();
        ViewBag.ResumeOptions = resumes;
        return resumes;
    }

    /// <summary>A posted resume id that isn't one of the user's own versions is dropped (treated as "None").</summary>
    private static void KeepOwnedResume(JobApplication app, List<ResumeVersion> owned)
    {
        if (app.ResumeVersionId.HasValue && owned.All(r => r.Id != app.ResumeVersionId.Value))
            app.ResumeVersionId = null;
    }

    /// <summary>Id → display name for every resume the user has, so list rows and the drawer can name the one used.</summary>
    private async Task<Dictionary<int, string>> ResumeLabelsAsync(string uid)
    {
        var resumes = await _context.ResumeVersions.AsNoTracking().Where(r => r.UserId == uid).ToListAsync();
        return resumes.ToDictionary(r => r.Id, r => r.DisplayName);
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

    // ── Reminders: mark contacted / snooze ────────────────

    /// <summary>
    /// Stamps <c>LastContactAt = now</c> (and clears any pending snooze) so the follow-up clock
    /// restarts. Called by the Edit page's plain form POST (redirects back to Edit with a toast)
    /// and by the drawer / dashboard via fetch with <c>X-Requested-With</c> (returns JSON).
    /// </summary>
    [HttpPost("JobApplications/{id:int}/contacted")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> MarkContacted(int id) =>
        UpdateReminderAsync(id, app =>
        {
            app.LastContactAt = DateTime.UtcNow;
            app.FollowUpAt    = null;
        }, "Marked as contacted today.");

    /// <summary>Pushes the follow-up reminder out by <see cref="ReminderService.SnoozeDays"/> days (sets <c>FollowUpAt</c> to that day's midnight in the user's zone).</summary>
    [HttpPost("JobApplications/{id:int}/snooze")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Snooze(int id)
    {
        var clock = await _clocks.GetAsync();
        return await UpdateReminderAsync(id, app => app.FollowUpAt = clock.StartOfLocalDayUtc(clock.Today.AddDays(ReminderService.SnoozeDays)),
            $"Follow-up snoozed for {ReminderService.SnoozeDays} days.");
    }

    private bool IsAjax => Request.Headers.XRequestedWith == "XMLHttpRequest";

    private async Task<IActionResult> UpdateReminderAsync(int id, Action<JobApplication> apply, string toast)
    {
        var app = await FindOwnedAsync(id);
        if (app is null)
            return IsAjax ? NotFound(new { success = false, error = "Application not found." }) : NotFound();

        apply(app);
        await _context.SaveChangesAsync();

        if (!IsAjax)
        {
            TempData["Toast"] = "success|" + toast;
            return RedirectToAction(nameof(Edit), new { id });
        }

        // Re-evaluate so the caller can update chips/rows without reloading.
        var clock  = await _clocks.GetAsync();
        var window = await _reminders.FollowUpAfterDaysAsync(UserId());
        var items  = ReminderService.Evaluate(app, window, clock).ToList();
        return Ok(new
        {
            success        = true,
            id             = app.Id,
            message        = toast,
            lastContactAt  = app.LastContactAt.HasValue ? clock.LocalDate(app.LastContactAt) : null,
            followUpAt     = app.FollowUpAt.HasValue ? clock.LocalDate(app.FollowUpAt) : null,
            followUpDue    = items.Any(i => i.Kind == ReminderKind.FollowUpDue),
            needsAttention = items.Count > 0
        });
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

        var clock = await _clocks.GetAsync();
        var notes = await _context.ApplicationNotes
            .Where(n => n.JobApplicationId == appId && n.UserId == uid)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new { n.Id, n.Text, n.CreatedAt })
            .ToListAsync();

        return Json(notes.Select(n => new { n.Id, n.Text, createdAt = clock.LocalDateTime(n.CreatedAt) }));
    }

    // ── Keyword coverage ─────────────────────────────────

    public sealed class KeywordCoverageRequest
    {
        /// <summary>An existing application to read the stored posting from (drawer, Edit).</summary>
        public int? AppId { get; set; }

        /// <summary>A posting the user is still typing, when there is no application id yet (Create).</summary>
        public string? Description { get; set; }

        /// <summary>
        /// The Create form's own company / role / location, so the employer's name and city are filtered out
        /// of the terms exactly as they are for a saved application. Ignored when <see cref="AppId"/> is set.
        /// </summary>
        public string? Company { get; set; }
        public string? Role { get; set; }
        public string? Location { get; set; }
    }

    /// <summary>
    /// Which of a posting's literal terms the user's active resume does not contain — the ATS-style
    /// counterpart to the AI match score. Deterministic, so there is no <c>"ai"</c> rate-limit policy and no
    /// demo carve-out: <see cref="KeywordCoverageService"/> makes no model call.
    ///
    /// Two shapes, one action. With <c>appId</c> it reads that application's stored posting (owner-scoped,
    /// 404 on a miss like every other id-taking endpoint). Without one it uses the posted description, which
    /// is how the Create form checks a posting that has not been saved yet. Either way the resume is the
    /// caller's own active version, so the result always reflects the resume that is active right now.
    /// </summary>
    /// <returns>
    /// JSON <c>{ available, reason, covered, total, missing: [{ term, count, inRequirements, context,
    /// nearMiss }] }</c>. <c>available</c> false means <c>reason</c> is a one-line explanation to show
    /// instead of a list.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> KeywordCoverage([FromForm] KeywordCoverageRequest request, CancellationToken ct)
    {
        var uid = UserId();

        var coverage = request.AppId is int id
            ? await _keywords.GetAsync(id, uid, ct)
            : await _keywords.GetForDescriptionAsync(request.Description, request.Company, request.Role, request.Location, uid, ct);

        if (coverage is null) return NotFound();

        return Json(new
        {
            coverage.Available,
            coverage.Reason,
            coverage.Covered,
            coverage.Total,
            Missing = coverage.Missing.Select(m => new
            {
                m.Term,
                m.Count,
                m.InRequirements,
                m.Context,
                m.NearMiss
            })
        });
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

        var clock = await _clocks.GetAsync();
        return Json(new { note.Id, note.Text, createdAt = clock.LocalDateTime(note.CreatedAt) });
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
            // Imported rows are treated like any new application: linked to the active resume.
            var activeResumeId = await _context.ResumeVersions
                .Where(r => r.UserId == uid && r.IsActive)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync();
            foreach (var app in result.Applications)
                app.ResumeVersionId = activeResumeId;

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
