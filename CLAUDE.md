# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                  # compile
dotnet run                    # start dev server at http://localhost:5240
dotnet run --no-build         # start without recompiling (faster restart)

# EF Core migrations
dotnet ef migrations add <Name>   # create a new migration
dotnet ef database update         # apply pending migrations to app.db
```

There are no tests at this time.

## Secrets / API Keys

The OpenAI API key is stored in .NET User Secrets, not in `appsettings.json`. The key in `appsettings.json` is a placeholder. To run the AI features locally:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
```

The `UserSecretsId` is already set in the `.csproj`. User Secrets override `appsettings.json` automatically in Development. Never commit real keys — `appsettings.json` is gitignored.

## Architecture

ASP.NET Core 9 MVC app with SQLite via EF Core and ASP.NET Identity scaffolded in.

**Request flow:** Browser → MVC controller action → `ApplicationDbContext` (EF Core) → `app.db` (SQLite) → Razor view rendered with Bootstrap 5.

**Key data model:** `Models/JobApplication.cs` — one table, owned by `UserId` (Identity user string). Status and WorkMode are C# enums in `Models/Enums/`.

**Database:** SQLite file at `app.db` in the project root. Connection string is `Data Source=app.db` in `appsettings.json`. Migrations live in `Data/Migrations/`.

**Auth:** ASP.NET Identity is wired up (`AddDefaultIdentity`) with `RequireConfirmedAccount = true`. Identity UI pages are scaffolded via Razor Pages under the `Identity` area. `JobApplications` routes are currently not `[Authorize]`-protected — `UserId` is set manually by the Create form (temporary; should be pulled from `User.FindFirstValue(ClaimTypes.NameIdentifier)` once auth is enforced).

**View layer:**
- `Views/Shared/_Layout.cshtml` — single layout; active nav links detected via `ViewContext.RouteData`.
- `Views/Home/Index.cshtml` — hero/marketing page (no model).
- `Views/Home/Dashboard.cshtml` — bound to `DashboardViewModel` (total count + per-status counts + recent 8 apps).
- `Views/JobApplications/` — Index (table) and Create (form) only; no Edit/Delete yet.
- `Models/ViewModels/DashboardViewModel.cs` — the only view model; everything else passes the EF entity directly.

**Static assets:** Bootstrap 5 and jQuery are vendored in `wwwroot/lib/`. Custom styles are in `wwwroot/css/site.css` (CSS custom properties, no preprocessor).

## Database Rules

- **Never modify `app.db` directly** — always use EF Core migrations or the application UI for data changes.
- **Never change user passwords or `EmailConfirmed` in the database** — these must only be modified through the proper Identity UI flows (password reset, email confirmation pages).
- **Never create new user accounts for testing purposes** — use the application's registration flow or Identity scaffolded pages to create test accounts.
