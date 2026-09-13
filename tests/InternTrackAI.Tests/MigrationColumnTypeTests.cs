using System.Text.RegularExpressions;
using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace InternTrackAI.Tests;

/// <summary>
/// Replays the whole migration set through the Npgsql migrations SQL generator (no database needed)
/// and checks the store type every DateTime / DateTimeOffset column ends up with on PostgreSQL.
/// Guards against the SQLite-scaffold trap where a column is created as TEXT and every read then
/// throws <c>InvalidCastException</c> in production (see FixApplicationNoteCreatedAtType).
/// </summary>
public class MigrationColumnTypeTests
{
    private const string Provider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string Expected = "timestamp with time zone";

    private static readonly Regex CreateTable  = new("CREATE TABLE \"(?<table>\\w+)\" \\((?<body>.*?)\\);", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ColumnLine   = new("^\\s*\"(?<col>\\w+)\"\\s+(?<type>[a-zA-Z][\\w ]*?(?:\\(\\d+(?:,\\s*\\d+)?\\))?)(?=\\s+NOT NULL|\\s+NULL|\\s+DEFAULT|\\s+GENERATED|\\s+CONSTRAINT|,|\\s*$)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex AddColumn    = new("ALTER TABLE \"(?<table>\\w+)\" ADD \"(?<col>\\w+)\" (?<type>[a-zA-Z][\\w ]*?(?:\\(\\d+(?:,\\s*\\d+)?\\))?)(?=\\s+NOT NULL|\\s+NULL|\\s+DEFAULT|\\s+GENERATED|;|\\s*$)", RegexOptions.Compiled);
    private static readonly Regex AlterType    = new("ALTER TABLE \"(?<table>\\w+)\"\\s+ALTER COLUMN \"(?<col>\\w+)\"\\s+TYPE\\s+(?<type>[a-zA-Z][\\w ]*?(?:\\(\\d+(?:,\\s*\\d+)?\\))?)(?=\\s+USING|;|\\s*$)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DropColumn   = new("ALTER TABLE \"(?<table>\\w+)\" DROP COLUMN \"(?<col>\\w+)\"", RegexOptions.Compiled);
    private static readonly Regex DropTable    = new("DROP TABLE \"(?<table>\\w+)\"", RegexOptions.Compiled);

    private static bool IsDateTime(Type t) => Nullable.GetUnderlyingType(t) is { } u ? IsDateTime(u) : t == typeof(DateTime) || t == typeof(DateTimeOffset);

    /// <summary>Runs every migration's Up through the Npgsql generator and returns (table.column → final store type) for date/time columns.</summary>
    private static (Dictionary<string, string> Types, List<string> Sql) Replay()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened")
            .Options;
        using var db = new ApplicationDbContext(options);

        var assembly    = db.GetService<IMigrationsAssembly>();
        var generator   = db.GetService<IMigrationsSqlGenerator>();
        var initializer = db.GetService<IModelRuntimeInitializer>();

        var dateColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // "Table.Column" with a DateTime/DateTimeOffset CLR type
        var types       = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allSql      = new List<string>();

        foreach (var (id, typeInfo) in assembly.Migrations.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            var migration = assembly.CreateMigration(typeInfo, Provider);

            foreach (var op in migration.UpOperations)
            {
                switch (op)
                {
                    case CreateTableOperation ct:
                        foreach (var c in ct.Columns.Where(c => IsDateTime(c.ClrType))) dateColumns.Add($"{ct.Name}.{c.Name}");
                        break;
                    case AddColumnOperation ac when IsDateTime(ac.ClrType):
                        dateColumns.Add($"{ac.Table}.{ac.Name}");
                        break;
                }
            }

            // Same call the real Migrator makes, so type resolution (explicit type → target model → provider default) matches production.
            var model = migration.TargetModel is null ? null : initializer.Initialize(migration.TargetModel);
            foreach (var command in generator.Generate(migration.UpOperations, model))
            {
                var sql = command.CommandText;
                allSql.Add($"-- {id}\n{sql}");

                foreach (Match t in CreateTable.Matches(sql))
                    foreach (Match c in ColumnLine.Matches(t.Groups["body"].Value))
                        types[$"{t.Groups["table"].Value}.{c.Groups["col"].Value}"] = c.Groups["type"].Value.Trim();
                foreach (Match m in AddColumn.Matches(sql))
                    types[$"{m.Groups["table"].Value}.{m.Groups["col"].Value}"] = m.Groups["type"].Value.Trim();
                foreach (Match m in AlterType.Matches(sql))
                    types[$"{m.Groups["table"].Value}.{m.Groups["col"].Value}"] = m.Groups["type"].Value.Trim();
                foreach (Match m in DropColumn.Matches(sql))
                    types.Remove($"{m.Groups["table"].Value}.{m.Groups["col"].Value}");
                foreach (Match m in DropTable.Matches(sql))
                    foreach (var key in types.Keys.Where(k => k.StartsWith(m.Groups["table"].Value + ".", StringComparison.OrdinalIgnoreCase)).ToList())
                        types.Remove(key);
            }
        }

        var result = dateColumns.ToDictionary(c => c, c => types.TryGetValue(c, out var t) ? t : "<not found in generated SQL>", StringComparer.OrdinalIgnoreCase);
        return (result, allSql);
    }

    [Fact]
    public void Every_DateTime_column_is_timestamptz_on_PostgreSQL()
    {
        var (types, _) = Replay();

        Assert.True(types.Count >= 11, $"expected to track at least 11 date/time columns, found {types.Count}: {string.Join(", ", types.Keys)}");
        Assert.Contains("ApplicationNotes.CreatedAt", types.Keys);
        Assert.Contains("AspNetUsers.LockoutEnd", types.Keys);
        Assert.Contains("JobApplications.InterviewAt", types.Keys);

        var wrong = types.Where(kv => !kv.Value.Equals(Expected, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(wrong.Count == 0, "Wrong PostgreSQL type for: " + string.Join("; ", wrong.Select(kv => $"{kv.Key} = {kv.Value}")));
        Assert.DoesNotContain(types, kv => kv.Value.Equals("text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fix_migration_is_a_noop_on_SQLite_and_tolerates_blank_values_on_PostgreSQL()
    {
        var sqliteOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").Options;
        using (var db = new ApplicationDbContext(sqliteOptions))
        {
            var assembly = db.GetService<IMigrationsAssembly>();
            var fix = assembly.CreateMigration(assembly.Migrations.Single(m => m.Key.EndsWith("_FixApplicationNoteCreatedAtType")).Value, "Microsoft.EntityFrameworkCore.Sqlite");
            Assert.Empty(fix.UpOperations);
            Assert.Empty(fix.DownOperations);
        }

        var (_, sql) = Replay();
        var fixSql = string.Join("\n", sql.Where(s => s.Contains("FixApplicationNoteCreatedAtType")));
        Assert.Contains("ALTER COLUMN \"CreatedAt\" TYPE timestamp with time zone", fixSql);
        Assert.Contains("USING COALESCE(NULLIF(btrim(\"CreatedAt\"::text), '')::timestamp with time zone, now())", fixSql);
        Assert.Contains("ALTER COLUMN \"LockoutEnd\" TYPE timestamp with time zone", fixSql);
        Assert.Contains("USING NULLIF(btrim(\"LockoutEnd\"::text), '')::timestamp with time zone", fixSql);
        Assert.Contains("ADD GENERATED BY DEFAULT AS IDENTITY", fixSql);
    }

    /// <summary>Tables added after FixPostgreSqlAutoIncrement must declare their own identity, or inserts fail with 23502 on Postgres.</summary>
    [Theory]
    [InlineData("GmailConnections")]
    [InlineData("StatusSuggestions")]
    public void New_tables_get_an_identity_key_on_PostgreSQL(string table)
    {
        var (_, sql) = Replay();
        var create = sql.FirstOrDefault(s => s.Contains($"CREATE TABLE \"{table}\""));
        Assert.NotNull(create);
        Assert.Matches("\"Id\" (integer|INTEGER) GENERATED BY DEFAULT AS IDENTITY", create);
    }
}
