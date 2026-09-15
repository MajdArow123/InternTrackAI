# CLAUDE.md

Guidance for Claude Code sessions in this repo. Every statement below was checked against the source on 2026-09-15; where something could not be verified from the repo it says so.

## 1. What this is

InternTrackAI is an AI-assisted internship/job application tracker for people running an internship search, replacing the spreadsheet: a pipeline of applications (list, Kanban board, detail drawer) plus GPT-4o-mini tools that fill forms from postings, score resume fit, write cover letters and interview prep, and turn Gmail messages into status suggestions. Portfolio project by Majd Arow, deployed on Railway.

- Live: https://interntrackai-production.up.railway.app (URL from README.md).
- Shared demo account behind the landing page's "Try the live demo" button (`POST /Account/DemoLogin`, credentials from `Demo:Email`/`Demo:Password`). `Services/DemoResetService.cs` reseeds it nightly **only when `Demo__AutoReset=true`**; the code/appsettings default is `false`. The Railway value is not visible from the repo.

## 2. Stack and versions

- ASP.NET Core 9 MVC + Razor views (`net9.0`), ASP.NET Identity (scaffolded Razor Pages in `Areas/Identity`), EF Core 9.
- SQLite locally (`Data Source=app.db`); PostgreSQL in production. `Program.cs` switches to Npgsql when the `DATABASE_URL` env var is set (hand-parses the `postgres://` URI; SSL disabled for `*.railway.internal`, required otherwise).
- Front end: Bootstrap 5.3.3, vanilla JS (no SPA framework), jQuery 3.7.1 + jquery-validation (unobtrusive validation), Chart.js 4.4.1, SortableJS 1.15.6, jsPDF 2.5.1 — all vendored in `wwwroot/lib/`, no npm.
- AI: OpenAI Chat Completions, model `gpt-4o-mini`, raw `HttpClient` (no OpenAI SDK). PDF text via UglyToad.PdfPig.
- Gmail: Google.Apis.Gmail.v1 + Google.Apis.Auth. Logging: Serilog (console). Hosting: Docker on Railway.
- Tests: xUnit + `Microsoft.AspNetCore.Mvc.Testing`. Playwright is **not** in the repo (see section 10).

`InternTrackAI.csproj` packages:

| Package | Version |
|---|---|
| Google.Apis.Auth | 1.76.0 |
| Google.Apis.Gmail.v1 | 1.75.0.4225 |
| Microsoft.AspNetCore.DataProtection.EntityFrameworkCore / Diagnostics.EntityFrameworkCore / Identity.EntityFrameworkCore / Identity.UI | 9.0.20 |
| Microsoft.EntityFrameworkCore.Design / .Sqlite / .Tools | 9.0.20 |
| Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore | 9.0.20 |
| Microsoft.VisualStudio.Web.CodeGeneration.Design | 9.0.12 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 9.0.4 |
| Serilog.AspNetCore | 9.0.0 |
| UglyToad.PdfPig | 1.7.0-custom-5 |
| Microsoft.Build.Tasks.Core 17.14.28, NuGet.Packaging 6.14.3, NuGet.Protocol 6.14.3 | transitive security pins (`PrivateAssets=all`) |

`tests/InternTrackAI.Tests/InternTrackAI.Tests.csproj`: Microsoft.AspNetCore.Mvc.Testing 9.0.20, Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, xunit.runner.visualstudio 3.1.5.

No Razor runtime compilation package: after editing a `.cshtml`, rebuild and restart (kill whatever holds :5240 first). CSS/JS changes are served immediately.

## 3. Commands

```bash
dotnet run                                   # http://localhost:5240 (launchSettings "http" profile)
dotnet test InternTrackAI.sln                # full xUnit suite (what CI runs)
dotnet ef migrations add <Name>              # scaffolds against SQLite — read "Migrations" in section 8 first
dotnet ef database update                    # not normally needed: Program.cs migrates on startup
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
OpenAI__ApiKey=your-openai-api-key-here dotnet run   # placeholder key: every AI service short-circuits, no spend
```

The installed global `dotnet-ef` is 10.0.2 against EF Core 9.0.20 packages.

## 4. Solution layout

| Path | Purpose |
|---|---|
| `Program.cs` | All DI, DB provider switch, auto-migrate, forwarded headers, pipeline, `/health` |
| `Areas/Identity/Pages/Account/` | Scaffolded Login, Register, ForgotPassword, ResetPassword(+Confirmation), Manage (Index, ChangePassword, DeletePersonalData). Identity UI also serves its non-scaffolded library pages (Manage/Email, EnableAuthenticator, ExternalLogins, …); `Areas/Identity/DemoAccountGuardFilter.cs` guards the Manage folder for the demo account |
| `Controllers/` | 14 MVC controllers (section 5) |
| `Data/ApplicationDbContext.cs` | DbSets, FK behaviours, UTC `DateTime` converters |
| `Data/Migrations/` | EF migrations, authored on SQLite, applied to Postgres in prod |
| `Helpers/` | View helpers: `StatusDisplay` (badges/icons/tiers), `ResumeDisplay`, `ProfileDisplay` (initials/photo URL) |
| `Models/` | Entities; `Models/Enums/` (`ApplicationStatus`, `WorkMode`, `SuggestionState`); `Models/ViewModels/` (10 view models/DTOs) |
| `Services/` | Business logic, AI clients, background jobs; `Services/Gmail/` for the Gmail integration |
| `Views/` | Razor views and partials; `Views/Shared/_Layout.cshtml` (app) and `_AuthLayout.cshtml` (Identity pages) |
| `wwwroot/css/site.css` | The design system (tokens + all styles) |
| `wwwroot/js/` | One file per page/feature (section 8) |
| `wwwroot/lib/` | Vendored front-end libraries |
| `tests/InternTrackAI.Tests/` | xUnit: unit tests at the root, WebApplicationFactory tests + fakes in `Integration/`; excluded from the main csproj globs |
| `docs/screenshots/` | README screenshots. No other docs exist yet (`docs/deployment.md` does not exist) |
| `.github/workflows/ci.yml` | restore, build Release, test on push/PR to `main` (ubuntu, .NET 9) |
| `Dockerfile`, `railway.toml` | Railway build and deploy config |

Gitignored and local only: `app.db`, `uploads/`, `appsettings.json` (placeholder key), `*.zip`, `.claude/settings.local.json`. `appsettings.Development.json` and `appsettings.Production.json` are committed.

## 5. Controllers

No global authorization fallback: anything without `[Authorize]` is anonymous. "ai" = `[EnableRateLimiting("ai")]`.

| Controller | Routes | Owns / notes |
|---|---|---|
| `HomeController` | `/`, `/Home/Dashboard`, `/Home/Privacy`, `/Home/NotFound`, `/Home/Error` | Landing (anonymous, `_HeroPreview` partial); Dashboard is the only `[Authorize]` action. `NotFound` is the status-code re-execute target |
| `AccountController` | `POST /Account/DemoLogin` | `[AllowAnonymous]`; signs out any current user and signs into `Demo:Email`. Buttons hidden when demo keys are unset |
| `JobApplicationsController` | Index, Board, Create, Edit, Delete (GET/POST), BulkDelete, BulkStatus, Notes (GET `?appId`), AddNote, Export, ImportCsv; attribute routes `POST /JobApplications/{id}/move`, `/reorder`, `/{id}/contacted`, `/{id}/snooze` | `[Authorize]`. Core CRUD. `apps_view` cookie redirects Index to Board. Posted `UserId` is ignored and restamped from claims. Duplicate company+role (trimmed, case-insensitive) warns unless `forceCreate`/`forceSave`. Delete/BulkDelete remove linked cover letters and prep sessions first |
| `AnalyzerController` | `POST /Analyzer/Analyze` | `[Authorize]`, ai, `[IgnoreAntiforgeryToken]` (JSON fetch, no writes). Calls `JobAnalyzerService` |
| `ProfileController` | `/Profile`, `/Profile/Bookmarklet`, SaveInfo, SaveReminderSettings, RegenerateCalendarToken, UploadPhoto, RemovePhoto, SaveSkills, SaveTargetRoles, UploadResume, SetActiveResume, RenameResume, DeleteResume, DownloadResume/{id}, AnalyzeResume (ai), ScoreResume (ai), AutoMatch (ai, `[IgnoreAntiforgeryToken]`) | `[Authorize]`. Profile, photo (2 MB, jpg/jpeg/png/webp), resumes (PDF, 5 MB, magic bytes checked), tags, calendar token. UploadResume is **not** rate-limited so the upload always succeeds; it takes one permit from `AiUsageLimiter` manually for auto-fill (skipped entirely on the demo account) |
| `CoverLetterController` | `/CoverLetter/Generate`, `GenerateAjax` (ai), `ImproveAjax` (ai), Save, Delete/{id}, SetActive/{id}, Download/{id} (.txt) | `[Authorize]`. AJAX actions keep `[ValidateAntiForgeryToken]` (token sent by JS) |
| `InterviewPrepController` | `/InterviewPrep/Prep?appId`, `Generate` (ai), `CritiqueAnswer` (ai) | `[Authorize]`. One `InterviewPrepSession` per application, overwritten on regenerate; critiques not stored |
| `FollowUpController` | `POST /JobApplications/{id}/followup`, `POST /JobApplications/{id}/followup/improve` | `[Authorize]`, ai, `[ValidateAntiForgeryToken]` (header token from `follow-up.js`), JSON. Owner-scoped 404; only `Applied` applications (`success:false` otherwise). Demo account: canned `FollowUpService.DemoDraft` (`demo:true`), improve refused with `demoRestricted:true`; both actions are `[NoAiCallForDemo]`, so neither takes a demo permit. Persists nothing |
| `SalaryInsightController` | `POST /SalaryInsight/Estimate` | `[Authorize]`, ai. Used on Create and Edit |
| `CaptureController` | `GET /Capture?url=&title=` | `[Authorize]`, ai. Bookmarklet target: validates http(s) URL ≤ 2048 chars, runs the analyzer with a `Capture:AnalyzeTimeoutSeconds` cap (default 15), redirects to Create with query-string prefill (`CapturePrefill`). Falls back to URL+title with an info toast. Saves nothing |
| `CalendarController` | `GET /Calendar/feed.ics?token=` (`[AllowAnonymous]`), `GET /Calendar/application/{id}.ics` (`[Authorize]`) | Feed is anonymous because calendar apps fetch without cookies; the 43-char `UserProfile.CalendarToken` is the secret, wrong/missing token = 404. Uses the token owner's time zone |
| `IntegrationsController` | `/Integrations/Gmail/Connect`, `/Callback`, `POST /Sync`, `POST /Disconnect` | `[Authorize]`. All 404 unless `Google:ClientId` + `Google:ClientSecret` are set. OAuth state = data-protected `{userId|nonce|expiry}` cookie (`itai_gmail_state`, 10 min). Connect refused for the demo account. Disconnect revokes with Google, deletes pending suggestions, keeps accepted/dismissed |
| `SuggestionsController` | `POST /Suggestions/{id}/accept`, `/dismiss` | `[Authorize]`, JSON; delegates to `SuggestionService` |
| `AdminController` | `GET/POST /Admin/ResetDemo` | `[Authorize]` + email must equal `Admin:Email`, else 404 (endpoint not advertised). POST runs `DemoSeeder.ResetAsync` regardless of `Demo:AutoReset` |

Also anonymous: `/health` (`MapHealthChecks(...).AllowAnonymous()`), Identity Login/Register/Forgot/Reset pages.

## 6. Services

OpenAI callers are marked **[AI]**. All read `OpenAI:ApiKey` and treat blank or `your-openai-api-key-here` as "not configured". Only `ProfileExtractorService`, `ResumeScoreService`, `FollowUpService` and `OpenAiStatusClassifier` honour `OpenAI:BaseUrl` (stub endpoint for local verification); the rest hardcode `https://api.openai.com`.

- `ConfiguredAccounts.cs` — the only way to read/match `Demo:Email` and `Admin:Email`: trims config values, compares ignoring case and whitespace, resolves users via `FindByEmailAsync` then `FindByNameAsync`. Used by DemoSeeder, DemoResetService, AdminController, AccountController, `AiRateLimiting.IsDemoEmail`/`IsDemoUser`, `DemoAccountGuardFilter` and the Manage page models (`IsDemoUser`, `DemoUnavailableMessage`).
- `AiRateLimiting.cs` — `AiRateLimitOptions`, singleton `AiUsageLimiter` (per-user fixed-window buckets), `NoAiCallForDemoAttribute`, and the `"ai"` policy + 429 handling. Registered via `AddAiRateLimiting` in Program.cs.
- `JobAnalyzerService.cs` **[AI]** — text or URL (fetches page via named client `UrlFetcher`, strips HTML, 8000 chars) → company, role, location, salary, skills, deadline, interview date. `AnalyzeAsync` is `virtual` with a CancellationToken so tests subclass it. Called by AnalyzerController, CaptureController.
- `ResumeMatcherService.cs` **[AI]** — resume text vs job description → score 0–100, tier (`RecommendationFor`: ≥60 APPLY, ≥40 MAYBE, ≥20 CONSIDER SKIPPING, else SKIP), matching/missing skills, strengths, summary. Static `ExtractPdfText` (PdfPig) is used by Profile, CoverLetter and InterviewPrep controllers. Called by `ProfileController.AutoMatch`.
- `ResumeScoreService.cs` **[AI]** — job-independent resume score, strengths, improvements. `ProfileController.ScoreResume`.
- `CoverLetterGeneratorService.cs` **[AI]** — `GenerateAsync` (letter dated with the user's local today) and `ImproveAsync`. CoverLetterController.
- `FollowUpService.cs` **[AI]** — follow-up email drafts. `BuildContextAsync` (owner-scoped) assembles company/role/location/mode, DateApplied, LastContactAt (user zone), job description (3000 chars), DisplayName→FullName, skills, target roles, `MatchingSkillsJson`, active resume text (2500, emails/phones/links scrubbed), this application's cover letter (active else newest, 1500) and the latest 10 notes. Pure `BuildGeneratePrompt`/`BuildImprovePrompt` + `Situation` (FIRST vs SECOND FOLLOW-UP when LastContactAt ≥ DateApplied; LONG WAIT at ≥ `LongWaitDays` 30), untrusted text in tagged sections with tag look-alikes stripped, `Parse` (JSON `{subject, body}`, one markdown fence tolerated, anything else → `BadFormatError`), `DemoDraft`. Logs only application id + token count. FollowUpController.
- `InterviewPrepService.cs` **[AI]** — 8–10 questions (`InterviewQuestion` records) and `CritiqueAnswerAsync`. InterviewPrepController.
- `SalaryInsightService.cs` **[AI]** — range + note from model knowledge (not live data). SalaryInsightController.
- `ProfileExtractorService.cs` **[AI]** — `IProfileExtractor`: name, skills, target roles from resume text. Used only via ProfileAutoFillService.
- `ProfileAutoFillService.cs` — reads a stored resume, calls `IProfileExtractor`, merges add-never-remove (name only if empty). ProfileController UploadResume/AnalyzeResume. Rate limiting is the caller's job.
- `ProfileTags.cs` — the single definition of "same tag" (trim, collapse whitespace, case-insensitive, first casing wins); JSON helpers. Mirrored client-side by `wwwroot/js/tag-input.js`.
- `ReminderService.cs` — the only place for "needs attention" rules: Overdue (deadline passed, still Saved), DeadlineSoon (≤7 days, not Offer/Rejected), FollowUpDue (Applied, anchor = later of DateApplied/LastContactAt, ≥ `FollowUpAfterDays` 3–30 default 7, not snoozed), UpcomingInterview (≤14 days). Snooze = 3 days. Used by HomeController (Attention card), JobApplicationsController (list filter, board/drawer chips via `ViewBag.AttentionById`, mark contacted/snooze), ProfileController (window bounds). The calendar feed does not use it; `IcsBuilder` emits raw dates.
- `IcsBuilder.cs` — hand-written RFC 5545 (escaping, 75-octet folding, VTIMEZONE from host tz data). CalendarController.
- `UserClock.cs` — `UserClock` (pure UTC↔zone conversion + formatting) and scoped `UserClockProvider` (per-request cached; `ForUserAsync` for the anonymous feed). Injected into every view as `Clocks`.
- `TimeZones.cs` — IANA catalogue/dropdown groups; `Resolve` falls back to `America/Toronto` for unknown ids.
- `ResumeAnalyticsService.cs` — "Resume performance": per resume version, sent (status ≠ Saved), responses (currently Interview or Offer), rate, low confidence under 5; card shows at ≥ 2 resumes. HomeController, ProfileController.
- `SkillGapService.cs` — "Skills you're missing most": aggregates stored `MissingSkillsJson`, no AI. HomeController. Rules in section 12.
- `CsvImportParser.cs` — pure parser for the Export column layout; skips rows without company/role, drops duplicates. `JobApplicationsController.ImportCsv`.
- `UploadStorage.cs` — singleton; root from `UPLOADS_PATH` (default `./uploads`). `photos/{userId}.{ext}` (public at `/uploads/photos`), `resumes/{userId}/{guid}.pdf` (private). `Resolve` rejects rooted/escaping stored paths and tolerates legacy `uploads/` prefixes.
- `UserDataPurger.cs` — deletes all of a user's rows and files; `keepProfile`/`keepActiveDocuments` for the demo reset. Used by `Areas/Identity/Pages/Account/Manage/DeletePersonalData.cshtml.cs` and DemoSeeder.
- `DemoSeeder.cs` — purges the demo user (keeps profile + active resume), ensures two resumes ("Backend focus" active, "General"), sets `TargetRoles` to exactly Software Engineer / Backend Systems Engineer / Frontend Product Engineer and `DisplayName` to "Demo User" (backstop for the display-name guard), seeds 15 applications, notes, 1 cover letter, 3 pending suggestions. AdminController, DemoResetService.
- `DemoResetService.cs` — hosted service; daily at `Demo:ResetTimeUtc` (default 04:00) only if `Demo:AutoReset` is true and `Demo:Email` is set; logs which at startup.
- `EmailSender.cs` — `ConsoleEmailSender` writes Identity emails to the log. Registered for **all** environments (see section 11).
- `GitHubService.cs` — unauthenticated GitHub REST, up to 6 public non-fork repos for the profile; 10 s timeout, null on any failure.
- `Services/Gmail/`:
  - `GmailOptions.cs` — `GoogleOptions` (`Google:*`), `GmailOptions` (`Gmail:*`: SyncIntervalMinutes 30, MaxMessagesPerSync 50, MaxBodyChars 4000, stub endpoint overrides), `GmailIntegration.IsConfigured`.
  - `GoogleOAuthClient.cs` / `IGoogleOAuthClient.cs` — auth URL (`gmail.readonly`, offline, prompt=consent), code exchange, refresh, revoke.
  - `GmailApiClient.cs` / `IGmailClient.cs` — read-only profile/list/get; maps headers, snippet, first text/plain part.
  - `GmailTokenProtector.cs` — Data Protection encryption of stored tokens.
  - `CompanyDomains.cs` — candidate sender domains from company name, job link, note emails; ignores generic mailboxes.
  - `StatusClassifier.cs` **[AI]** — `IStatusClassifier` / `OpenAiStatusClassifier`; only Interview/Offer/Rejected survive `Parse`.
  - `GmailSyncService.cs` — one user's sync: refresh token, query (`newer_than:30d` first, then `after:LastSyncedAt`), match message → application, classify (one `AiUsageLimiter` permit each), store `StatusSuggestion`. Called by IntegrationsController.Sync and the hosted service.
  - `GmailSyncHostedService.cs` — every `SyncIntervalMinutes`, per-user scope + try/catch; idle when Google keys are absent.
  - `SuggestionService.cs` — owner-scoped pending lists/counts, Accept (applies status + interview time, adds note "Status updated from email: …"), Dismiss. Used by Home, JobApplications, SuggestionsController, `_Layout.cshtml` nav badge.

## 7. Data model

All app tables key on `UserId` (Identity user id string) **without** an FK to `AspNetUsers`; account deletion goes through `UserDataPurger`. Enums are stored as ints — never reorder members of `ApplicationStatus` (Saved, Applied, Interview, Rejected, Offer), `WorkMode` (Remote, Hybrid, OnSite) or `SuggestionState` (Pending, Accepted, Dismissed). `ApplicationDbContext.ConfigureConventions` tags every `DateTime` as UTC on read and write (relabel, no shift).

| Entity (table) | Key fields | Relationships |
|---|---|---|
| `JobApplication` (JobApplications) | CompanyName, RoleTitle (≤100, stored trimmed), JobLink, Location, WorkMode, Status, Deadline & DateApplied (calendar dates), Salary, JobDescription, MatchScore (0–100, null = unanalyzed), MatchRecommendation, MatchSummary, MatchingSkillsJson, MissingSkillsJson, BoardOrder, InterviewAt, FollowUpAt, LastContactAt (UTC instants), ResumeVersionId | → ResumeVersion **SetNull** |
| `ApplicationNote` (ApplicationNotes) | JobApplicationId, UserId, Text ≤2000, CreatedAt | → JobApplication **Cascade**. Append-only, no edit/delete UI |
| `StatusSuggestion` (StatusSuggestions) | ApplicationId, GmailMessageId (unique with UserId), SuggestedStatus, Confidence 0–1, Summary ≤300, InterviewAt, EmailSubject, EmailFrom, EmailDate, Status, CreatedAt. No email body stored | → JobApplication **Cascade**; index (UserId, Status) |
| `InterviewPrepSession` (InterviewPrepSessions) | JobApplicationId, QuestionsJson, GeneratedAt | → JobApplication **Cascade** (controller also deletes them explicitly) |
| `GeneratedCoverLetter` (GeneratedCoverLetters) | JobApplicationId (nullable), Content, CompanyName/RoleTitle snapshots, IsActive, VersionNumber | → JobApplication, no DB delete action (EF ClientSetNull). The model comment says letters outlive the application, but Delete/BulkDelete delete linked letters |
| `ResumeVersion` (ResumeVersions) | VersionNumber (renumbered 1..N on delete), OriginalFileName, StoredPath, FileSize, UploadedAt, IsActive (one per user, app-enforced), Label ≤60 | referenced by JobApplication |
| `UserProfile` (UserProfiles) | FullName, DisplayName, Country, PhoneNumber, PhotoFileName, PhotoVersion, SkillsJson, TargetRolesJson, GitHubUsername, FollowUpAfterDays (7), CalendarToken, TimeZoneId (`America/Toronto`) | one row per user, created lazily |
| `GmailConnection` (GmailConnections) | GmailAddress, AccessToken/RefreshToken (Data Protection ciphertext), TokenExpiresAt, LastSyncedAt, LastHistoryId (stored, unused) | unique index on UserId |
| `DataProtectionKey` (DataProtectionKeys) | Data Protection key ring, so cookies/antiforgery/Gmail tokens survive redeploys | — |

JSON columns (all `System.Text.Json`):
- `MatchingSkillsJson`, `MissingSkillsJson` — JSON array of plain strings, e.g. `["Docker","Go"]`; `null` when never analyzed; `"[]"` means analyzed with nothing found. Written client-side by `wwwroot/js/application-form.js` (`JSON.stringify` of the matcher response) into hidden inputs on Create, round-tripped as hidden inputs on Edit, and by `DemoSeeder`. The caps (matching max 10, missing max 8) exist only in the `ResumeMatcherService` prompt; nothing server-side enforces length or shape — readers must tolerate malformed values (`SkillGapService.ParseSkills` treats them as unanalyzed).
- `SkillsJson`, `TargetRolesJson` — JSON array of strings, deduped via `ProfileTags`; empty list stored as `null`.
- `QuestionsJson` — array of `{"Category","Question","Tip"}` (PascalCase, default serializer; read case-insensitively). Category is Technical / Behavioral / Company-Specific.

## 8. Cross-cutting rules

**Ownership.** Every action taking a record id loads it with `Id == id && UserId == currentUser` and returns `NotFound()` (JSON `{success:false,error}` for fetch endpoints) on a miss — never 403, and never a silent no-op. 404 makes "not yours" indistinguishable from "doesn't exist", so ids can't be probed. Bulk endpoints silently ignore foreign ids. Foreign ids in bodies (e.g. `applicationId` on cover letter Save, `ResumeVersionId` on Create/Edit) are rejected or dropped. Every new id-taking endpoint gets a test in `tests/InternTrackAI.Tests/Integration/OwnershipTests.cs` (or the feature's endpoint test file).

**Configured accounts.** Never read `Demo:Email`/`Admin:Email` with `config[...]` or compare them with `string.Equals`/`==`; go through `Services/ConfiguredAccounts.cs`. Railway variables can carry surrounding whitespace, which Identity's lookups don't trim, and a LINQ `u.Email == x` is case-sensitive on both providers. Look users up only through `UserManager` (normalised columns).

**Rate limiting.** Policy `"ai"` (`Services/AiRateLimiting.cs`): one fixed window per user id (IP for anonymous), `RateLimiting:AI:PermitLimit` 20 per `WindowMinutes` 60, `DemoPermitLimit` 10 when the signed-in email equals `Demo:Email`. Shared across every AI endpoint. Rejection: 429 + `Retry-After`; JSON `{success:false, hasResume:true, rateLimited:true, error}` for JSON/XHR callers, otherwise redirect to the local referrer with an error toast. Work outside a request draws from the same `AiUsageLimiter` bucket: each Gmail classification, and resume-upload auto-fill. Every new OpenAI call must be tagged `[EnableRateLimiting(AiRateLimiting.PolicyName)]` or take a permit manually. `[NoAiCallForDemo]` on an "ai" action makes the policy hand the demo account a no-limit partition there; use it only when the action's demo branch returns before any model call (today: FollowUp Generate/Improve, pinned by `RateLimitTests.Demo_permit_exemption_is_only_on_the_reviewed_canned_actions`). Resume auto-fill skips its manual permit for the demo account in `ProfileController.AutoFillToastAsync`, and the demo can't connect Gmail, so no other demo path draws a permit without an AI call.

**Forwarded headers.** Railway terminates TLS and forwards plain HTTP. `UseForwardedHeaders` (For, Proto, Host) must stay the **first** middleware, and `KnownNetworks`/`KnownProxies` stay cleared: the defaults trust loopback only, Railway's proxy connects from a private address, and the headers were silently ignored (Gmail `redirect_uri_mismatch`, http:// reset links). All absolute URLs come from `Request.Scheme`/`Request.Host` (Gmail redirect URI unless `Google:RedirectBaseUrl` is set, password-reset links, bookmarklet code, calendar feed URL) and must come out https in production — guarded by `Integration/ForwardedProtoTests.cs`. `UseHttpsRedirection` runs only in Development (it would loop behind the proxy).

**Time.** Store UTC; convert only through `UserClock`. In views: `var clock = await Clocks.GetAsync();`. Calendar dates (Deadline, DateApplied) are never shifted: `clock.Date(...)`. Instants (InterviewAt, FollowUpAt, LastContactAt, note/upload/generation stamps): `clock.LocalDate/LocalDateTime/LocalTime`. Create/Edit forms bind InterviewAt/FollowUpAt with `asp-for`, so the controller converts the entity before render and after post (`UtcToForm`/`FormToUtc` in JobApplicationsController). `UserClock.InputDateTime/InputDate` exist but are currently unused. "Today" for any N-days rule is `clock.Today`. Never `DateTime.Now`, never `.ToString(...)` a DateTime for display in a view (the one existing raw format, `Views/JobApplications/Edit.cshtml` LastContactAt hidden input, is a UTC round-trip, not display).

**Migrations.** `dotnet ef migrations add` scaffolds against SQLite: DateTime columns come out `type: "TEXT"` (on Postgres that becomes a `text` column: inserts coerce, every read throws `InvalidCastException`), `double` as `REAL` (float4 on Postgres), and new tables get only `Sqlite:Autoincrement`, so Postgres inserts fail with 23502. Rule: every migration that adds/alters a DateTime, floating-point or identity column must branch on `migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"` (`timestamp with time zone` / `double precision` vs `TEXT` / `REAL`) and add `.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)` to new identity keys. Templates: `20260913225431_AddGmailConnections.cs`, `20260913230212_AddStatusSuggestions.cs`. Verify with `tests/InternTrackAI.Tests/MigrationColumnTypeTests.cs`, which replays all migrations through the Npgsql SQL generator (no DB): it fails automatically on any non-timestamptz date column, but the identity check only covers tables listed in its `[InlineData]` — add every new table there. Optional real-Postgres check: `Integration/PostgresMigrationTests.cs` with `INTERNTRACK_PG_CONNECTION` set (drops and recreates that database). `PendingModelChangesWarning` is suppressed on Npgsql in Program.cs because the snapshot is SQLite-flavoured.

**Views and JS.** Large views are split into partials (`Views/Home/_*.cshtml`, `Views/JobApplications/_*.cshtml`, `Views/Profile/_*.cshtml`, `Views/CoverLetter/_*.cshtml`). Page behaviour lives in `wwwroot/js/<page>.js`, one IIFE per file, loaded from the view's `Scripts` section; no new globals. Server data reaches JS via `data-*` attributes or JSON islands (`<script type="application/json" id="appsOverTimeData">`, `id="skillGapData"` in `Views/Home/Dashboard.cshtml`). No new inline `<script>` blocks. Existing exceptions, don't copy them: theme pre-paint in `_Layout.cshtml` and `_AuthLayout.cshtml`, toast + theme toggle + keyboard shortcuts in `_Layout.cshtml`, and the inline IIFE still in `Views/InterviewPrep/Prep.cshtml`. Intentional shared globals: `site.js` functions (`showAppToast`, `appConfirm`, `shakeField`, …), `window.TagInput`, `window.reminderAction`, `window.suggestionAction`. Confirm dialogs: add `data-confirm` to a form (global styled confirm in `_Layout`). Toasts from the server: `TempData["Toast"] = "success|message"` (also `info`, `error`).

**Design.** `wwwroot/css/site.css` is the only design system: tokens on `:root`, dark overrides on `html[data-theme="dark"]` (theme stored in `localStorage.theme`). Use tokens for every new colour; no new palettes, no second design system, no inline colour styles. Some older rules still hold literals (e.g. `.btn-danger { background:#DC2F27 }` for 4.7:1 contrast, dark navbar rgba values, Chart.js gradient rgba in `dashboard.js`); `Views/Shared/_Layout.cshtml.css` is leftover template scoped CSS, still bundled via `InternTrackAI.styles.css`. Tooltip/popover gotcha: Bootstrap re-declares `--bs-tooltip-*` on `.tooltip` itself, so bridge tokens there; the app does not use `data-bs-theme`. Sticky/fixed gotchas: the sticky element is `body > header` (a sticky `.site-nav` can't leave its same-height parent); its measured height is `--site-nav-h` (set by `site.js`), used by the Applications table header and `scroll-padding-top`. Entrance animations must not keep a transform after they end (`.anim-fade-up` uses fill-mode `backwards`): any retained transform on `<main>`, even `translateY(0)`, traps the drawer, bulk bar and modals (`position: fixed`) inside it and under the navbar. Chromium paints no outer `box-shadow` on table header cells; use an `inset` shadow or a pseudo-element.

| Token | Light | Dark |
|---|---|---|
| `--bg` | #EEEEF1 | #0D0D0F |
| `--card` | #FBFBFD | #1C1C1E |
| `--surface-2` / `--surface-3` | #F4F4F7 / #EFEFF2 | #232326 / #2C2C2E |
| `--text` | #1D1D1F | #F5F5F7 |
| `--text-2`, `--muted` | #6E6E73 | #98989D |
| `--muted-2` | #AEAEB2 | #636366 |
| `--border` / `--border-strong` | rgba(0,0,0,.06) / rgba(0,0,0,.12) | rgba(255,255,255,.08) / rgba(255,255,255,.14) |
| `--accent` / `--accent-h` / `--accent-rgb` | #0A84FF / #0071E3 / 10,132,255 | inherited |
| `--accent-soft` / `--accent-text` | rgba(10,132,255,.10) / #0066CC | rgba(10,132,255,.16) / #409CFF |
| `--success` / `-soft` / `-text` | #30D158 / rgba(48,209,88,.14) / #1B8F3A | inherited / inherited / #30D158 |
| `--warning` / `-soft` / `-text` | #FF9F0A / rgba(255,159,10,.16) / #B36B00 | inherited / inherited / #FFB340 |
| `--danger` / `-soft` / `-text` | #FF453A / rgba(255,69,58,.12) / #C62F26 | inherited / inherited / #FF6961 |
| `--purple` / `-soft` / `-text` | #BF5AF2 / rgba(191,90,242,.14) / #8944AB | inherited / inherited / #DA8FFF |
| `--neutral-soft` / `--neutral-text` | rgba(142,142,147,.14) / #48484A | rgba(142,142,147,.18) / #AEAEB2 |
| `--tooltip-bg` / `-color` / `-border` | #1D1D1F / #F5F5F7 / transparent | #3A3A3C / #F5F5F7 / rgba(255,255,255,.12) |
| `--dark` | #1D1D1F | #3A3A3C |
| `--primary`, `--primary-h` | legacy aliases of accent | — |

Non-colour tokens: `--radius` 16px, `--radius-md` 12px, `--radius-sm` 10px, `--radius-pill` 999px, `--shadow-xs/-sm/-/-lg` and `--focus-ring` (both themes), `--ease`, `--duration` 180ms, `--font`.

**Dependencies.** No new NuGet packages, vendored libraries or CDN scripts without asking the user first.

**Working rules from the user.**
- Never edit `app.db` directly; data changes go through migrations or the app UI.
- Never change passwords or `EmailConfirmed` in the database; never create accounts outside the app's Register page. Verify with throwaway accounts registered through the UI, never the demo password.
- Don't trigger real OpenAI calls locally (a real key sits in user-secrets): run with the placeholder `OpenAI__ApiKey`, or a stub via `OpenAI__BaseUrl` for the four services that honour it.

## 9. Features as built

Applications
- List with search/status/work-mode/sort filters and a "Needs attention" pill — `JobApplicationsController.Index`, `_Filters`, `_Table`, `wwwroot/js/applications.js`.
- Kanban board, drag/keyboard move, per-column order — `Board`, `move`, `reorder`, `_BoardCard`, `wwwroot/js/board.js` (SortableJS).
- Detail drawer (posting, AI analysis, reminders, notes timeline, suggestions, add-to-calendar); deep link `/JobApplications#open-{id}` — `_Drawer`, `wwwroot/js/app-drawer.js`, `Notes`/`AddNote`.
- Bulk delete/status, compare 2–3 selected rows side by side (client-side over rendered rows) — `BulkDelete`, `BulkStatus`, `_BulkBar`, `applications.js`.
- CSV export/import — `Export`, `ImportCsv`, `Services/CsvImportParser.cs`, `_ImportModal`.
- Duplicate warning on Create/Edit — `IsDuplicateAsync`, `wwwroot/js/duplicate-warning.js`.

AI tools (all rate-limited, section 8)
- Job analyzer on Create — `AnalyzerController` → `JobAnalyzerService`; `_AnalyzerPanel`, `application-form.js`.
- Resume match scoring after analysis, persisted via hidden inputs — `ProfileController.AutoMatch` → `ResumeMatcherService`.
- Cover letters (generate, improve, save versions, set active, .txt download, jsPDF download client-side) — `CoverLetterController`, `CoverLetterGeneratorService`, `cover-letter.js`.
- Follow-up email drafts: "Draft follow-up" beside Mark contacted / Snooze on FollowUpDue Attention rows and in the drawer (list + board; shown only while the row's `data-follow-up-due` is set, hidden again after Mark contacted/Snooze); modal with skeleton, subject + copy, editable body, improve/regenerate (empty instruction = fresh draft), inline error + Retry, focus trap — `FollowUpController`, `FollowUpService`, `Views/Shared/_FollowUpModal.cshtml`, `follow-up.js`. The board card itself has no button (it has no Mark contacted/Snooze), nor does Edit.
- Interview prep + answer critique — `InterviewPrepController`, `InterviewPrepService`.
- Salary insight on Create/Edit — `SalaryInsightController`, `SalaryInsightService`, `salary-insight.js`.
- Resume score — `ProfileController.ScoreResume`, `ResumeScoreService`.
- Profile extraction (auto after upload, manual "Analyze with AI") — `ProfileAutoFillService`, `ProfileExtractorService`, `profile.js`, `tag-input.js`.

Tracking and insight
- Dashboard: stat cards, applications-over-time chart, funnel/top companies, Attention card (max 8), onboarding, recent 8 — `HomeController.Dashboard`, `Views/Home/_*.cshtml`, `dashboard.js`.
- Reminders (mark contacted, snooze) — `ReminderService`, `contacted`/`snooze` endpoints, `reminders.js`, `Views/Profile/_Reminders.cshtml`.
- Calendar feed + per-application .ics — `CalendarController`, `IcsBuilder`, `Views/Profile/_Calendar.cshtml`.
- Per-user time zone — `UserClock`, `TimeZones`, `ProfileController.SaveInfo`.
- Resume performance card + profile per-resume stats, inline rename — `ResumeAnalyticsService`, `_ResumePerformance`.
- Skill gap card (role pills, drill-down list) — `SkillGapService`, `_SkillGaps`, `skill-gap.js`.
- Gmail status suggestions (dashboard card, drawer, board dot, nav badge) — `Services/Gmail/*`, `IntegrationsController`, `SuggestionsController`, `_InboxSuggestions`, `_ConnectedAccounts`, `suggestions.js`.
- Bookmarklet capture — `CaptureController`, `ProfileController.Bookmarklet`, `BookmarkletViewModel`, `bookmarklet.js`.

Account, demo, admin
- Profile: photo (avatar is the upload control), basic info, skills/roles chips, resume versions, GitHub repos — `ProfileController`, `GitHubService`.
- Account deletion — `DeletePersonalData` page + `UserDataPurger`. Password reset via Identity + console email.
- Demo login — `AccountController.DemoLogin`. Demo reset — `DemoSeeder` + `DemoResetService`. Admin reset — `AdminController.ResetDemo`.
- Dark mode toggle and keyboard shortcuts (`?`, N, D, C, P, S, Esc) — `_Layout.cshtml`. Landing hero interactive preview — `_HeroPreview`, `landing-preview.js`.
- There is **no job feed** feature (no controller, service, or view for one).

## 10. Testing

- Run: `dotnet test InternTrackAI.sln`. Current count: **512 tests, all passing** (2026-09-15, ~19 s; the 3 `PostgresMigrationTests` are no-ops unless `INTERNTRACK_PG_CONNECTION` is set). CI runs the same on every push/PR to `main`.
- `Integration/TestAppFactory.cs` boots the real `Program` in environment `Testing` against a private SQLite in-memory connection and a temp `UPLOADS_PATH`. Helpers and fakes in `Integration/`: `Http.cs` (antiforgery token scraping, `RegisterAsync` via the real Register page), `GmailFakes.cs` (`FakeGoogleOAuthClient`, `FakeGmailClient`, `FakeStatusClassifier`, `GmailTestHost`), `FakeProfileExtractor.cs`, `TestPdf.cs` (generates real text PDFs). Replace services with `WithWebHostBuilder` + `RemoveAll`. Assert on `WebUtility.HtmlDecode`d HTML (Razor entity-encodes non-ASCII).
- Tests must never reach OpenAI or Google: the `Testing` environment loads no user-secrets, so no API key is present, and Google-facing clients are faked. Keep it that way.
- Convention: every new endpoint that takes an id gets an ownership test (foreign user's id → 404, data unchanged). Existing ones: `OwnershipTests.cs`, `FollowUpEndpointTests.cs`, `BoardEndpointTests.cs`, `CalendarTests.cs`, `SuggestionEndpointTests.cs`, `GmailConnectTests.cs`. Gap: `Profile/RenameResume` has no foreign-id test.
- Migration guard: `MigrationColumnTypeTests.cs` (section 8).
- Playwright: not part of the repo, no committed scripts or package.json. Browser verification is done ad hoc from a session scratchpad using gstack's install (`~/.claude/skills/gstack/node_modules/playwright`, symlink `node_modules` into the scratchpad; browsers cached in `~/Library/Caches/ms-playwright`). User rule: any refactor of the Applications views needs a Playwright click-through of every action on the page. Seed data through the UI (Register, CSV import, Create form hidden inputs), not the database.

## 11. Deployment

- Railway builds `Dockerfile` (sdk:9.0 publish → aspnet:9.0, `ENTRYPOINT dotnet InternTrackAI.dll`). `railway.toml`: `healthcheckPath = "/health"`, timeout 100, restart ON_FAILURE ×3.
- PostgreSQL via Railway-injected `DATABASE_URL`; `PORT` is injected and bound in Program.cs.
- `db.Database.Migrate()` runs on every startup, so shipping a migration is a push.
- Uploads: `UPLOADS_PATH` points at a mounted Railway volume (README: volume at `/data`, `UPLOADS_PATH=/data/uploads`; not verifiable from the repo).
- `/health`: 200 `{"status":"Healthy",...}` when the DB answers, 503 otherwise; logged at Verbose.
- Production has no `appsettings.json` (gitignored); defaults live in code and `appsettings.Production.json`. Configure with `__` env vars.
- Exception handler `/Home/Error` + HSTS outside Development; `UseStatusCodePagesWithReExecute("/Home/NotFound")` everywhere.

Environment variables (to move to `docs/deployment.md` later; the README table is the other copy):

| Variable | Default | Purpose |
|---|---|---|
| `DATABASE_URL`, `PORT` | injected | Postgres switch, listen port |
| `OpenAI__ApiKey` | — | All AI features |
| `OpenAI__BaseUrl` | api.openai.com | Stub for extractor, scorer, follow-up drafts, Gmail classifier only; local verification |
| `UPLOADS_PATH` | `./uploads` | Upload root |
| `Demo__Email`, `Demo__Password` | — | Demo login button; demo rate limit; demo guards |
| `Demo__AutoReset`, `Demo__ResetTimeUtc` | `false`, `04:00` | Nightly demo reseed |
| `Admin__Email` | — | Only account allowed on `/Admin/ResetDemo` |
| `RateLimiting__AI__PermitLimit` / `__WindowMinutes` / `__DemoPermitLimit` | 20 / 60 / 10 | AI bucket |
| `Capture__AnalyzeTimeoutSeconds` | 15 | Bookmarklet analyzer cap |
| `Google__ClientId`, `Google__ClientSecret` | — | Enable Gmail integration |
| `Google__RedirectBaseUrl` | derived | Pin the OAuth redirect origin |
| `Gmail__SyncIntervalMinutes` / `__MaxMessagesPerSync` / `__MaxBodyChars` | 30 / 50 / 4000 | Sync tuning |
| `Gmail__AuthorizationEndpoint`, `__TokenEndpoint`, `__RevokeEndpoint`, `__ApiBaseUri` | Google | Stub endpoints for verification only |
| `Serilog__MinimumLevel__Default` | Information | Log level override |
| `INTERNTRACK_PG_CONNECTION` | — | Tests only: enables `PostgresMigrationTests` |

## 12. Known gaps and deliberate decisions

Deliberate — do not "fix":
- Email is console-only: `ConsoleEmailSender` is the `IEmailSender` in every environment, so reset links go to the log. Its doc comment says "Development-only"; that is stale, the registration is intentional for now. `RequireConfirmedAccount = false`.
- The public profile (`/p/{slug}`, commit c155efe, migration `RemovePublicProfile`) and uploaded cover letters on the profile (bbb5f48, `RemoveUploadedCoverLetters`) were removed on purpose; profile Age too (`RemoveAgeFromProfile`). Don't reintroduce them. AI-generated cover letters are a separate, current feature.
- Skill gap role matching (`SkillGapService.MatchesRole`) is deterministic and literal-word based: a strict majority of the tag's non-filler words must appear in the role title after a fixed suffix table (`Stem`) and compound joining. No synonyms, role families, fuzzy distance or AI. Commit daf2533 relaxed it once, from "every word" to this strict-majority rule; that is the agreed rule. Do not loosen it further or add synonyms to make demo data split nicely — change the seed (`DemoSeeder.TargetRoles`) instead. Only Applied-or-later applications count; `"[]"` counts as analyzed; card hidden under 3 analyzed.
- Google OAuth consent screen stays in Testing mode (100 test users); only the `gmail.readonly` scope is ever requested; email bodies are never stored.
- Demo account: Gmail Connect refused, resume auto-fill skipped, tighter AI limit, Draft follow-up returns the canned `FollowUpService.DemoDraft` and refuses improve, without taking a demo AI permit (`[NoAiCallForDemo]`). On Identity/Account/Manage, `DemoAccountGuardFilter` (registered in Program.cs on the whole folder) refuses every POST to credential pages (change password, set password, change email, delete account, all 2FA pages, external logins) with a redirect to Manage and a "Not available on the demo account." toast; ChangePassword and DeletePersonalData render read-only with the notice, the library pages redirect. Manage/Index renders read-only (display name form disabled, POST refused); `ProfileController.SaveInfo` refuses a demo display-name change (JSON `field: "displayName"`) and keeps the stored name when the disabled input is omitted. PersonalData stays open. `DemoAccountGuardTests.Every_manage_page_is_either_guarded_or_explicitly_allowed` fails if an Identity UI upgrade adds an unclassified page.
- No status history: analytics use current status (Interview/Offer = response).
- Antiforgery failures and other 400s surface as 404 because status-code pages re-execute through `/Home/NotFound`.

Genuinely unfinished or known issues:
- Compare modal prints Status as the enum integer (`applications.js` reads `data-status`, rendered as `(int)item.Status` in `_Table.cshtml`).
- Forgot/Reset password and ConfirmEmailChange are not demo-guarded; they need a token that is only ever written to the server log (console email).
- `Views/InterviewPrep/Prep.cshtml` still carries an inline script (~170 lines) not yet moved to `wwwroot/js`.
- `GmailConnection.LastHistoryId` is recorded but incremental `history.list` sync is not implemented.
- Skill caps on matcher output are prompt-only; no server-side validation of the JSON skill columns.
- `OpenAI:BaseUrl` is ignored by the analyzer, matcher, cover letter, interview prep and salary services.
- `Profile/RenameResume` lacks an ownership test.
- `GeneratedCoverLetter` XML comment claims letters survive application deletion; the controller deletes them.
