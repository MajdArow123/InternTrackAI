using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using InternTrackAI.Data;
using InternTrackAI.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<ApplicationDbContext>();

// Email service for password reset tokens
builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();

// ── Application services ────────────────────────────────────────────────────
builder.Services.AddHttpClient<JobAnalyzerService>();
builder.Services.AddHttpClient<ResumeMatcherService>();
builder.Services.AddHttpClient<ResumeScoreService>();
builder.Services.AddHttpClient<CoverLetterGeneratorService>();
builder.Services.AddHttpClient<InterviewPrepService>();
builder.Services.AddHttpClient<ProfileExtractorService>();
builder.Services.AddHttpClient<SalaryInsightService>();
builder.Services.AddHttpClient<GitHubService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("UrlFetcher")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddControllersWithViews();

// By default, Data Protection keys live in memory/on local disk and are lost whenever
// the container restarts or redeploys, which silently invalidates every existing
// antiforgery token and auth cookie (forcing all logged-in users to sign in again).
// Persisting keys to the database instead means they survive redeploys.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

// ── Port (Railway injects PORT) ──────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://+:{port}");

var app = builder.Build();

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
// Railway's edge proxy terminates HTTPS and forwards plain HTTP to the container,
// attaching X-Forwarded-* headers describing the original request. Without this,
// the app would think every request is HTTP and misreport the client IP. This must
// run before the HTTPS-redirect/HSTS checks below so they see the real scheme.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

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
app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

app.Run();
