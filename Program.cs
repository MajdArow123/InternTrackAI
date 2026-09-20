using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using InternTrackAI.Data;
using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using InternTrackAI.Services;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Events;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ── Logging (Serilog → console) ──────────────────────────────────────────────
// Defaults are set in code so production (which has no appsettings.json in the image) gets sane
// levels; a "Serilog" configuration section can override any of them. Nothing here logs request
// bodies, headers, cookies, or query strings, so credentials and API keys never reach the log.
builder.Host.UseSerilog((context, loggerConfig) => loggerConfig
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Migrations", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "InternTrackAI")
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

// ── Database ────────────────────────────────────────────────────────────────
// Railway sets DATABASE_URL for the attached PostgreSQL service.
// Fall back to SQLite for local development.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(databaseUrl))
{
    // Npgsql doesn't accept the postgres:// URI form directly, so it has to be
    // hand-parsed into a key=value connection string: postgres://user:pass@host:port/dbname
    var uri      = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':', 2);
    // Traffic on Railway's private network (*.railway.internal) never leaves their
    // datacenter and doesn't support/require SSL; the public hostname does.
    var sslMode  = uri.Host.EndsWith(".railway.internal") ? "Disable" : "Require;Trust Server Certificate=true";
    var npgsql   = $"Host={uri.Host};Port={uri.Port};" +
                   $"Database={uri.AbsolutePath.TrimStart('/')};" +
                   $"Username={Uri.UnescapeDataString(userInfo[0])};" +
                   $"Password={Uri.UnescapeDataString(userInfo[1])};" +
                   $"SSL Mode={sslMode}";
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
    {
        options.UseNpgsql(npgsql);
        // Migrations are authored/snapshotted against SQLite locally but applied to
        // Npgsql in production. EF Core's design-time tooling can't fully reconcile the
        // two providers' model snapshots, which trips a "pending model changes" warning
        // that doesn't reflect an actual schema drift — silence it.
        options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    });
}
else
{
    // No DATABASE_URL means we're running locally — use the SQLite file referenced
    // in appsettings.json instead of requiring a Postgres instance for dev.
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseSqlite(connectionString));
}

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ── Identity ────────────────────────────────────────────────────────────────
// Wires up ASP.NET Core Identity (registration, login, password reset, etc.) backed
// by ApplicationDbContext. RequireConfirmedAccount = false means users can sign in
// immediately after registering, without first confirming their email address.
builder.Services.AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;

        // Login.cshtml.cs passes lockoutOnFailure, so these bounds are what actually stops password
        // guessing: 5 wrong passwords buy a 15-minute wait. Each attempt costs the server a
        // 100k-iteration PBKDF2 hash, so the cap is a CPU guard as much as a credential one. The
        // shared demo account is exempted at the call site, not here — see Login.cshtml.cs.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers      = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>();

// ── Email ────────────────────────────────────────────────────────────────────
// Resend:ApiKey switches real sending on. Without it the console sender takes over and reset links are
// written to the log, which is what local development runs on. The key is trimmed because Railway variables
// can carry a trailing newline. The app logs which of the two is live just after Build().
var resendApiKey = builder.Configuration["Resend:ApiKey"]?.Trim();
if (!string.IsNullOrEmpty(resendApiKey))
    builder.Services.AddHttpClient<IAppEmailSender, ResendEmailSender>()
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
else
    builder.Services.AddScoped<IAppEmailSender, ConsoleEmailSender>();

// Identity UI's compiled pages (email change, resend confirmation) resolve the plain interface, so both
// names have to reach the one implementation chosen above.
builder.Services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<IAppEmailSender>());

// ForgotPassword is anonymous and spends email quota, so it carries its own per-IP and per-address limits.
// Singleton because the buckets must outlive the request, like AiUsageLimiter.
builder.Services.Configure<PasswordResetRateLimitOptions>(
    builder.Configuration.GetSection(PasswordResetRateLimitOptions.SectionName));
builder.Services.AddSingleton<PasswordResetLimiter>();

// ── Upload storage ───────────────────────────────────────────────────────────
// Resolves the on-disk root for user uploads from UPLOADS_PATH (defaults to ./uploads).
// Singleton so the root is computed and created once at startup.
builder.Services.AddSingleton<UploadStorage>();

// Removes every row and file owned by a user; shared by account deletion and the demo reset.
builder.Services.AddScoped<UserDataPurger>();

// ── Demo account reset ───────────────────────────────────────────────────────
// DemoSeeder does the reseed; DemoResetService schedules it nightly but only arms itself when
// Demo:AutoReset is true and Demo:Email is set (see the startup log line). POST /Admin/ResetDemo
// runs the same reseed on demand for the Admin:Email account regardless of that flag.
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddHostedService<DemoResetService>();

// ── Application services ────────────────────────────────────────────────────
// Single source of truth for "needs attention" rules (follow-up due, deadline soon/overdue,
// upcoming interview); the dashboard, list, board, drawer and calendar feed all go through it.
builder.Services.AddScoped<ResumeTextService>();        // the only way to read a resume's text (extract once, store on the version)
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<ResumeAnalyticsService>();   // "Resume performance" card + profile stats
builder.Services.AddScoped<SkillGapService>();          // "Skills you're missing most" card (stored data only, no AI)
builder.Services.AddScoped<KeywordCoverageService>();   // ATS keyword coverage (deterministic, no AI)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<UserClockProvider>();

// ── Gmail integration (optional) ─────────────────────────────────────────────
// Google:ClientId / Google:ClientSecret switch the whole feature on. Without them every Gmail
// element is hidden, the Integrations endpoints answer 404 and the background sync stays idle.
// Tokens are stored encrypted (GmailTokenProtector); only the gmail.readonly scope is requested.
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.SectionName));
builder.Services.Configure<GmailOptions>(builder.Configuration.GetSection(GmailOptions.SectionName));
builder.Services.AddSingleton<GmailTokenProtector>();
builder.Services.AddSingleton<IGoogleOAuthClient, GoogleOAuthClient>();
builder.Services.AddSingleton<IGmailClient, GmailApiClient>();
builder.Services.AddHttpClient<IStatusClassifier, OpenAiStatusClassifier>();
builder.Services.AddScoped<GmailSyncService>();
builder.Services.AddScoped<SuggestionService>();   // dashboard card, drawer, board dot, navbar badge, accept/dismiss
builder.Services.AddHostedService<GmailSyncHostedService>();   // per-user time zone (see Services/UserClock.cs)
builder.Services.AddHttpClient<JobAnalyzerService>();
builder.Services.AddHttpClient<ResumeMatcherService>();
builder.Services.AddHttpClient<ResumeScoreService>();
builder.Services.AddHttpClient<CoverLetterGeneratorService>();
builder.Services.AddHttpClient<FollowUpService>();   // "Draft follow-up" modal (no storage)
builder.Services.AddHttpClient<ResumeRewriteService>();   // "Rewrite a bullet" on the profile Resume card (no storage)
builder.Services.AddHttpClient<InterviewPrepService>();
builder.Services.AddHttpClient<IProfileExtractor, ProfileExtractorService>();
builder.Services.AddScoped<ProfileAutoFillService>();
builder.Services.AddHttpClient<SalaryInsightService>();
builder.Services.AddHttpClient<GitHubService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("UrlFetcher")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddControllersWithViews();

// ── Response compression ─────────────────────────────────────────────────────
// This app ships unbundled, unminified CSS and JS by design (no npm, libraries vendored), so a
// first visit decodes 600-850 KB of text per page; site.css alone is 177 KB on the wire. Brotli
// first, gzip for anything that cannot take it.
//
// EnableForHttps is on deliberately. The BREACH/CRIME concern with compressing HTTPS responses
// needs a secret reflected into a compressed body alongside attacker-controlled input; the
// antiforgery token is the one such secret here, and ASP.NET Core sends it in a response header
// and a form field on pages that are already Cache-Control: no-store, with a per-request value.
// Railway terminates TLS and forwards plain HTTP, so without this flag compression would never
// apply in production at all - the whole point of adding it.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/json", "image/svg+xml", "text/calendar", "application/manifest+json",
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// The shared demo account can't change its password or email, turn on two-factor, link logins or delete itself.
// Applied to the whole Manage folder so Identity UI's built-in (non-scaffolded) pages are covered too.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AddAreaFolderApplicationModelConvention(
        "Identity", InternTrackAI.Areas.Identity.DemoAccountGuardFilter.ManageFolder,
        model => model.Filters.Add(new InternTrackAI.Areas.Identity.DemoAccountGuardFilter()));

    // Identity UI maps ResendEmailConfirmation even though this app never sends a confirmation email
    // (RequireConfirmedAccount = false). Anonymous and backed by IEmailSender, it was an unthrottled way to
    // make the app send real mail to any registered address; it now 404s. See UnusedIdentityPageFilter.
    foreach (var page in InternTrackAI.Areas.Identity.UnusedIdentityPageFilter.DisabledPages)
        options.Conventions.AddAreaPageApplicationModelConvention("Identity", page,
            model => model.Filters.Add(new InternTrackAI.Areas.Identity.UnusedIdentityPageFilter()));
});

// ── Health checks ────────────────────────────────────────────────────────────
// /health verifies the database connection (Railway's health check path points here).
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("database");

// ── Rate limiting for the OpenAI-backed endpoints ────────────────────────────
// One fixed-window bucket per user shared by every action tagged [EnableRateLimiting("ai")];
// limits come from the RateLimiting:AI section (see AiRateLimitOptions for defaults).
builder.Services.AddAiRateLimiting(builder.Configuration);

// By default, Data Protection keys live in memory/on local disk and are lost whenever
// the container restarts or redeploys, which silently invalidates every existing
// antiforgery token and auth cookie (forcing all logged-in users to sign in again).
// Persisting keys to the database instead means they survive redeploys.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

// ── Kestrel ─────────────────────────────────────────────────────────────────
// Kestrel stamps `Server: Kestrel` on every response by default. It tells anyone scanning which
// server (and therefore which advisories) to aim at, and nothing in the app or the proxy reads it,
// so it is turned off. Railway's edge proxy adds its own Server header in front of this one.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// ── Port (Railway injects PORT) ──────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://+:{port}");

var app = builder.Build();

// States at a glance whether password-reset links actually leave the server (see the Email region above).
if (!string.IsNullOrEmpty(resendApiKey))
    app.Logger.LogInformation("Email sending is ENABLED via Resend (from {From}).",
        builder.Configuration["Email:From"]?.Trim() ?? "(Email:From not set — sends will fail)");
else
    app.Logger.LogInformation("Email sending is DISABLED: reset links are written to the log. Set Resend:ApiKey to send real email.");

// ── Auto-migrate on startup ──────────────────────────────────────────────────
// Applies any pending EF Core migrations every time the app boots, so shipping a
// new migration is just a normal `git push` — no separate manual migration step
// against the production database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

// ── Proxy / forwarded headers (Railway terminates TLS at the load balancer) ──
// Railway's edge proxy terminates HTTPS and forwards plain HTTP to the container, attaching
// X-Forwarded-For / -Proto / -Host describing the original request. This MUST be the first
// middleware in the pipeline: everything after it (HSTS, auth cookies' Secure flag, Url.Action
// with Request.Scheme, the Gmail OAuth redirect_uri, password-reset links) reads Request.Scheme
// and Request.Host, and those are only correct once the forwarded values have been applied.
//
// KnownNetworks/KnownProxies are cleared on purpose. The defaults trust loopback only, and
// Railway's proxy connects from a private-network address, so with the defaults every
// X-Forwarded-* header was silently ignored and the app kept believing it was on http://.
// The container is only reachable through Railway's proxy, so trusting whichever hop sent the
// request is safe here; the proxy overwrites client-supplied X-Forwarded-* headers.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

// Security headers on everything, static files included — so this has to sit above UseStaticFiles,
// which short-circuits. Values and the reasoning for each live in Services/SecurityHeaders.cs; the
// CSP is report-only until the last inline scripts move out of _Layout and Prep.cshtml.
app.UseSecurityHeaders();

// Above UseStaticFiles for the same reason the headers are: that middleware short-circuits, and
// the static files are the bulk of what there is to compress.
app.UseResponseCompression();

// ── Request pipeline ────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    // EF Core's built-in UI for resolving pending-migration errors locally.
    app.UseMigrationsEndPoint();
}
else
{
    // Generic error page + HSTS (force HTTPS on subsequent visits) in production only.
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Custom 404/500 pages instead of the framework defaults.
app.UseStatusCodePagesWithReExecute("/Home/NotFound");

// Skip HTTPS redirect in production — Railway already terminates TLS at the proxy,
// so the app itself only ever receives plain HTTP and redirecting would loop.
if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseStaticFiles();

// ── Request logging ──────────────────────────────────────────────────────────
// One structured line per request (method, path, status, duration, user id). Placed after the
// static file middleware so asset requests don't flood the log; /health is logged at Verbose.
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms (user: {UserId})";
    options.GetLevel = (http, _, ex) =>
        ex != null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
        : LogEventLevel.Information;
    options.EnrichDiagnosticContext = (diagnosticContext, http) =>
    {
        diagnosticContext.Set("UserId", http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous");
    };
});

// Serve profile photos from the uploads root (which may be a mounted volume outside wwwroot)
// at the same /uploads/photos/... URL the views already use. Only the photos subfolder is
// exposed — resumes stay private and go through ProfileController.
var uploads = app.Services.GetRequiredService<UploadStorage>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploads.PhotosDirectory),
    RequestPath  = "/uploads/photos"
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
// After authorization so the limiter can key on the signed-in user; only endpoints that
// opt in with [EnableRateLimiting("ai")] are affected.
app.UseRateLimiter();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

// ── Health endpoint ──────────────────────────────────────────────────────────
// Anonymous. 200 {"status":"Healthy",...} when the database answers, 503 otherwise.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (http, report) =>
    {
        http.Response.ContentType = "application/json";
        var payload = new
        {
            status   = report.Status.ToString(),
            duration = report.TotalDuration.TotalMilliseconds,
            checks   = report.Entries.Select(e => new
            {
                name     = e.Key,
                status   = e.Value.Status.ToString(),
                duration = e.Value.Duration.TotalMilliseconds,
                error    = e.Value.Exception?.GetType().Name
            })
        };
        await http.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}).AllowAnonymous();

app.Run();

// Lets the test project reference the entry point via WebApplicationFactory<Program>.
public partial class Program { }
