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
   working deployment; everything else is optional — with the caveat that without `Resend__ApiKey` the
   password-reset link is only written to the log, so nobody can actually reset a password.
5. **Check the health path.** `railway.toml` sets `healthcheckPath = "/health"`. If you configure the
   service by hand instead, set **Settings → Deploy → Healthcheck Path** to `/health`.
6. **Optionally add a custom domain** — see [Custom domain](#custom-domain) below. Worth doing if the app
   sends email: a reset link on a shared `*.up.railway.app` subdomain inherits that subdomain's aggregate
   reputation, and putting the From domain and the link domain on one name you own is a strong signal.

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
suggestions and a saved cover letter. The profile row and active resume are kept, a second labelled resume
version is added, and the applications are split between the two so the Resume performance card has a real
comparison to show. Every profile field the seeder owns — name, country, display name, skills and target
roles — is restored rather than left as a visitor edited it, and the seeded skills are exactly the ones the
seeded match scores imply, so the profile never contradicts the skill gap card. The seeded missing skills
give that card a clear top skill, a middle tier and a tail.

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

### Email

Password reset is the only feature that sends email. `Resend__ApiKey` is what switches real sending on;
without it the app still runs and the reset link is written to the log instead. The startup log states
which of the two is live.

| Variable | Default | Purpose |
|---|---|---|
| `Resend__ApiKey` | — | Resend API key (`re_…`). Set → reset emails are really sent; unset → they go to the log only |
| `Email__From` | — | Sender, in `Name <address>` form, e.g. `InternTrackAI <noreply@majdarow.com>`. The domain must be verified in Resend or every send is rejected. Required whenever `Resend__ApiKey` is set |
| `Email__ReplyTo` | — | Optional address for replies |
| `Resend__BaseUrl` | `https://api.resend.com` | Stub endpoint for local verification only, like `OpenAI__BaseUrl` |
| `RateLimiting__PasswordReset__PerIpPerHour` | `5` | Reset requests one client may make per hour |
| `RateLimiting__PasswordReset__PerAddressPerHour` | `2` | Emails one address may receive per hour |
| `RateLimiting__PasswordReset__WindowMinutes` | `60` | Length of both windows |

**Resend setup.** Create a Resend account → **Domains → Add domain** → add the DKIM and SPF records it
prints at your DNS provider → wait for *Verified*. Then **API Keys → Create**, with *Sending access*, and
set the key as `Resend__ApiKey` in Railway along with `Email__From`.

While you are in the domain's settings, **confirm Click tracking is off**. Resend has no per-send tracking
switch — it is a domain-level setting — and with click tracking on, Resend rewrites the reset link into a
tracking redirect instead of leaving the raw URL. Both tracking options are off by default on a new domain.

The free tier is 100 emails/day and 3,000/month. Over it, Resend answers `daily_quota_exceeded`, which is
logged and not retried.

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
path, status, duration and user id. No bodies, headers, cookies or secrets are logged, and a failed email
send logs the status and Resend's error name but never the recipient, the message body or the reset link.

### Password reset email

The page is anonymous, so it is rate limited two ways: **5 requests per client per hour**, checked before the
account lookup so probing unknown addresses costs the prober as much as real ones, and **2 emails per address
per hour**, taken only when a message is really about to go out so probes can't burn a real user's allowance.
Addresses are never stored — the per-address bucket is keyed by a salted SHA-256 under a salt generated fresh
each process. A refused request renders the identical confirmation, with no 429 and no different wording: if
being rate limited looked any different, the limit would answer "does this address have an account?".

`/Identity/Account/ResendEmailConfirmation` is switched off (404). Identity UI maps it, but registration never
sends a confirmation (`RequireConfirmedAccount = false`), and left alone it was an anonymous, unthrottled way
to make the app mail any registered address. If confirmation is ever turned on, re-enable it in `Program.cs`
and give it the same limits as the reset page.

`ResendEmailSender` posts to Resend's API and retries once on a 5xx, a 429 or a network failure, never on
any other 4xx. Both attempts carry the same `Idempotency-Key`, so a retry after a lost response cannot
deliver a second email, and the whole thing is capped at 15 seconds so a Resend outage cannot hold the page
open. A failure is logged and swallowed: the page shows the same "check your email" confirmation whether the
address exists, belongs to the demo account, or the send failed — otherwise it would be a way to test which
addresses have accounts. A reset requested for `Demo__Email` sends nothing and mints no token.

### Background jobs

`GmailSyncHostedService` syncs every connected account on an interval, one account at a time so one expired
token cannot block the rest. It logs counts only, never email content, and stays idle when the Google keys
are absent. `DemoResetService` schedules the nightly reseed and logs at startup whether it is armed.

### Custom domain

The app answers on its original `*.up.railway.app` hostname **and** on any custom domain, at the same time.
Nothing in the code pins a hostname: every absolute URL it emits — the emailed reset link, the Gmail OAuth
`redirect_uri`, the bookmarklet's target origin, the calendar feed URL — is built from `Request.Scheme` and
`Request.Host`, so each one follows whichever host the request arrived on.
`ForwardedProtoTests.Every_absolute_url_follows_the_host_the_request_arrived_on` pins that for both.

To add one:

1. **Railway first.** Service → **Settings → Networking → Custom Domain** → enter the hostname. Railway
   responds with a CNAME target unique to that domain; you need it for step 2.
2. **Then DNS.** Add a `CNAME` for the subdomain pointing at the target Railway printed. On Cloudflare set
   **Proxy status to *DNS only* (grey cloud)** — Railway issues its own Let's Encrypt certificate and needs
   to reach the host to validate it. If you later turn the orange cloud on, Cloudflare's SSL/TLS mode must be
   **Full** or **Full (strict)**; *Flexible* makes Cloudflare talk http to Railway, which redirects to https,
   which loops.
3. **Wait for the certificate.** Railway shows the domain as issued/active. Until then the host serves a TLS
   error, not a 404.
4. **Google OAuth**, if the Gmail integration is on: add
   `https://<new-host>/Integrations/Gmail/Callback` to the OAuth client's **Authorized redirect URIs** and
   **keep the existing one**. The redirect URI is derived per request, so every live hostname needs an entry.

**Do not set `Google__RedirectBaseUrl` to force one origin while both hostnames are live.** The OAuth state
cookie (`itai_gmail_state`) is host-scoped — no `Domain` attribute — so a flow begun on host A and redirected
back to host B arrives without the cookie and the callback fails. Leave it unset and let each host derive its
own; pin it only if you retire the other hostname.

Two more consequences of running on two hostnames, neither of them a fault:

- **Sessions don't cross.** Auth cookies are host-scoped too, so signing in on one hostname leaves you signed
  out on the other. Data Protection keys live in the database, so the cookies themselves stay valid across
  deploys and both hosts can decrypt what they issued.
- **Password-reset links are bound to the host that issued them**, which is the host the user was on.

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
