using InternTrackAI.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Boots the real app (Program.cs, Identity, MVC, migrations) against a private SQLite
/// in-memory database and a throwaway uploads directory. The connection is held open for the
/// factory's lifetime because SQLite drops an in-memory database when its last connection closes.
/// </summary>
public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "interntrack-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_connection.State != System.Data.ConnectionState.Open) _connection.Open();

        builder.UseEnvironment("Testing");
        // appsettings.json is gitignored, so CI has no connection string; Program.cs requires one
        // to exist even though the DbContext registration is replaced below.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Data Source=:memory:");
        builder.UseSetting("UPLOADS_PATH", _uploads);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            try { Directory.Delete(_uploads, recursive: true); } catch { /* best effort */ }
        }
    }
}
