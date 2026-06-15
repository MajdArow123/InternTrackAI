using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using InternTrackAI.Data;
using InternTrackAI.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ────────────────────────────────────────────────────────────────
// Railway sets DATABASE_URL for the attached PostgreSQL service.
// Fall back to SQLite for local development.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(databaseUrl))
{
    // Parse postgres://user:pass@host:port/dbname
    var uri      = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':', 2);
    var npgsql   = $"Host={uri.Host};Port={uri.Port};" +
                   $"Database={uri.AbsolutePath.TrimStart('/')};" +
                   $"Username={Uri.UnescapeDataString(userInfo[0])};" +
                   $"Password={Uri.UnescapeDataString(userInfo[1])};" +
                   "SSL Mode=Require;Trust Server Certificate=true";
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(npgsql));
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseSqlite(connectionString));
}

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ── Identity ────────────────────────────────────────────────────────────────
builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<ApplicationDbContext>();

// ── Application services ────────────────────────────────────────────────────
builder.Services.AddHttpClient<JobAnalyzerService>();
builder.Services.AddHttpClient<ResumeMatcherService>();
builder.Services.AddHttpClient<ResumeScoreService>();
builder.Services.AddHttpClient<CoverLetterGeneratorService>();
builder.Services.AddHttpClient<InterviewPrepService>();
builder.Services.AddHttpClient("UrlFetcher")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddControllersWithViews();

// ── Port (Railway injects PORT) ──────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://+:{port}");

var app = builder.Build();

// ── Auto-migrate on startup ──────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

// ── Proxy / forwarded headers (Railway terminates TLS at the load balancer) ──
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// ── Request pipeline ────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/Home/NotFound");

// Skip HTTPS redirect in production — Railway handles TLS at the proxy level.
if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

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
