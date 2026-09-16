# Deployment

The live instance runs on [Railway](https://railway.app), built from the `Dockerfile` in this repo
(`sdk:9.0` publish → `aspnet:9.0` runtime). `railway.toml` already sets the health check path, its timeout
and the restart policy, so a fresh service mostly configures itself.

---

## Deploying your own copy

1. **Create a Railway project** and point it at a fork of this repo. Railway builds the `Dockerfile` on
   every push to `main`; no build command or start command needs setting.
2. **Add a PostgreSQL service.** Railway injects `DATABASE_URL`, which is all the app needs to switch
   providers — see [Database](#database) below.
3. **Attach a volume**, mounted at `/data`, and set `UPLOADS_PATH=/data/uploads`. Without this, uploaded
   resumes and photos live in the container filesystem and vanish on the next deploy.
4. **Set the environment variables** below. Only `OpenAI__ApiKey` and `UPLOADS_PATH` are needed for a
   working deployment; everything else is optional.
5. **Check the health path.** `railway.toml` sets `healthcheckPath = "/health"`. If you configure the
   service by hand instead, set **Settings → Deploy → Healthcheck Path** to `/health`.

---

## Environment variables

`DATABASE_URL` and `PORT` are injected by Railway automatically — do not set them yourself.

Note the double underscore: `Section__Key` is how .NET reads a nested configuration key from the
environment, so `OpenAI__ApiKey` here is `OpenAI:ApiKey` in `appsettings.json` or user secrets.

### Required

| Variable | Default | Purpose |
|---|---|---|
| `OpenAI__ApiKey` | — | OpenAI API key. Every AI feature checks it and degrades gracefully when it is blank or left as the `your-openai-api-key-here` placeholder, so the app still runs without one — the AI features just report that they are not configured |
| `UPLOADS_PATH` | `./uploads` | Upload root. Point at the mounted volume, e.g. `/data/uploads` |

### Demo account

| Variable | Default | Purpose |
|---|---|---|
| `Demo__Email` / `Demo__Password` | — | Credentials behind the **Try the live demo** button. Leave unset to hide the button entirely |
| `Demo__AutoReset` | `false` | When `true` (and `Demo__Email` is set) the demo account is wiped and reseeded nightly |
| `Demo__ResetTimeUtc` | `04:00` | Time of day, UTC, for that nightly reset |
| `Admin__Email` | — | The one account allowed to call `POST /Admin/ResetDemo` for a manual reseed. Unset disables the endpoint, which is never advertised in the UI |

The reseed purges the demo account and rebuilds it: 15 realistic applications, notes, three pending inbox
suggestions and a saved cover letter. The profile and active resume are kept, a second labelled resume
version is added, and the applications are split between the two so the Resume performance card has a real
comparison to show. Target roles and display name are reset, and the seeded missing skills give the skill
gap card a clear top skill, a middle tier and a tail.

Sign in as `Admin__Email` and open `/Admin/ResetDemo` to run it by hand and check the result before turning
the nightly job on. The startup log states whether auto-reset is armed.

### AI rate limiting

One fixed window per user, shared across every AI feature.

| Variable | Default | Purpose |
|---|---|---|
| `RateLimiting__AI__PermitLimit` | `20` | AI requests allowed per user per window |
| `RateLimiting__AI__WindowMinutes` | `60` | Length of the window |
| `RateLimiting__AI__DemoPermitLimit` | `10` | Tighter allowance for the shared demo account |

### Gmail integration

Both Google keys must be present or the whole feature stays hidden. Full walkthrough in
[gmail-setup.md](gmail-setup.md).

| Variable | Default | Purpose |
|---|---|---|
| `Google__ClientId` / `Google__ClientSecret` | — | Google OAuth web-client credentials |
| `Google__RedirectBaseUrl` | derived | Public origin used verbatim for the OAuth `redirect_uri`. Normally unnecessary — the app derives the https origin from Railway's `X-Forwarded-*` headers. Set it only if the redirect URI logged at the start of the flow differs from the one registered in the Google console |
| `Gmail__SyncIntervalMinutes` | `30` | How often connected accounts are synced in the background |
| `Gmail__MaxMessagesPerSync` | `50` | Messages examined per account per sync |
| `Gmail__MaxBodyChars` | `4000` | How much of a message body is sent for classification |

### Other

| Variable | Default | Purpose |
|---|---|---|
| `Capture__AnalyzeTimeoutSeconds` | `15` | How long the bookmarklet's `/Capture` endpoint waits for the AI analyzer before falling back to manual entry |
| `Serilog__MinimumLevel__Default` | `Information` | Log level override |

---

## How the deployment behaves

### Database

`Program.cs` looks for `DATABASE_URL` at startup. When it is present the app parses the `postgres://` URI
by hand into an Npgsql connection string and uses PostgreSQL; when it is absent it falls back to the SQLite
file in `appsettings.json`. SSL is disabled for `*.railway.internal` hosts (private networking, where it is
neither supported nor needed) and required everywhere else.

### Migrations run on startup

`db.Database.Migrate()` runs on every boot, so **shipping a migration is just a push**. There is no separate
migration step to run, and no window where the code is ahead of the schema.

### Uploads

Resumes and profile photos are written under `UPLOADS_PATH`. Resumes are never served as static files —
they are streamed back through a controller action that checks ownership first. Only profile photos are
public.

### Data Protection keys

Persisted to the database rather than the container filesystem, so antiforgery tokens, auth cookies and
encrypted Gmail tokens all survive restarts and redeploys.

### Health check

`GET /health` returns `{"status":"Healthy"}` with HTTP 200 when the database answers, and 503 otherwise.
It is logged at Verbose so the every-30-second probe does not flood the log.

### Logging

Serilog writes structured lines to the console, which Railway captures: one line per request with method,
path, status, duration and user id. No bodies, headers, cookies or secrets are logged.

### Background jobs

`GmailSyncHostedService` syncs every connected account on an interval, one account at a time so one expired
token cannot block the rest. It logs counts only, never email content, and stays idle when the Google keys
are absent. `DemoResetService` schedules the nightly reseed and logs at startup whether it is armed.

### Behind the proxy

Railway terminates TLS and forwards plain HTTP, so `UseForwardedHeaders` runs as the **first** middleware
with `KnownNetworks`/`KnownProxies` cleared — the defaults trust loopback only, and Railway's proxy connects
from a private address. Every absolute URL the app emits (the Gmail redirect URI, password-reset links, the
bookmarklet code, the calendar feed URL) comes from `Request.Scheme`/`Request.Host` and must therefore come
out `https` in production. `UseHttpsRedirection` runs only in Development, since behind the proxy it would
loop.

---

## The PostgreSQL migration gotcha

**Read this before adding a migration.** Migrations are scaffolded locally against SQLite but applied to
PostgreSQL in production, and the EF Core tooling emits SQLite-flavoured column types that are wrong — and
in two cases silently wrong — on Npgsql.

| Scaffolded as | On PostgreSQL | Consequence |
|---|---|---|
| `DateTime` → `type: "TEXT"` | a `text` column | Inserts coerce and appear to work; **every read throws `InvalidCastException`** |
| `double` → `type: "REAL"` | `float4` | Silent precision loss |
| New identity key → `Sqlite:Autoincrement` only | no identity | Inserts fail with `23502` (not-null violation) |

So any migration that adds or alters a **DateTime**, **floating-point** or **identity** column must branch:

```csharp
var isNpgsql = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";

migrationBuilder.AddColumn<DateTime>(
    name: "SyncedAt",
    table: "GmailConnections",
    type: isNpgsql ? "timestamp with time zone" : "TEXT",
    nullable: true);
```

and a new identity key needs
`.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)`.

Working templates: `Data/Migrations/20260913225431_AddGmailConnections.cs` and
`20260913230212_AddStatusSuggestions.cs`.

Plain strings, ints and bools need no branch — a nullable string is `TEXT` on SQLite and `text` on
PostgreSQL either way.

### How it's guarded

`tests/InternTrackAI.Tests/MigrationColumnTypeTests.cs` replays every migration through the Npgsql SQL
generator without touching a database, and fails on any non-`timestamptz` date column. Its identity check
only covers the tables listed in its `[InlineData]`, so **add every new table there**.

For a real round-trip against PostgreSQL, set `INTERNTRACK_PG_CONNECTION` and the three tests in
`Integration/PostgresMigrationTests.cs` come to life. They drop and recreate that database, so point them
at a scratch one.

`PendingModelChangesWarning` is suppressed on Npgsql in `Program.cs`: the model snapshot is SQLite-flavoured,
so EF reports a drift that does not exist.
