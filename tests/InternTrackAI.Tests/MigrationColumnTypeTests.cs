using System.Text.RegularExpressions;
using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace InternTrackAI.Tests;

/// <summary>
/// Replays the whole migration set through the Npgsql migrations SQL generator (no database needed)
/// and checks the store type every DateTime / DateTimeOffset / bool column ends up with on PostgreSQL.
/// Guards against the SQLite-scaffold trap where a column is created as TEXT (dates) or INTEGER (bools)
/// and every read then throws <c>InvalidCastException</c> in production — the traps
/// FixApplicationNoteCreatedAtType and FixPostgreSqlTypesAndAddDataProtection each had to go back and undo.
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
    private static bool IsBool(Type t) => Nullable.GetUnderlyingType(t) is { } u ? IsBool(u) : t == typeof(bool);

    /// <summary>Runs every migration's Up through the Npgsql generator and returns (table.column → final store type) for date/time columns.</summary>
    private static (Dictionary<string, string> Types,
                    Dictionary<string, string> Bools,
                    Dictionary<string, (Type Clr, string Sql)> Every,
                    List<string> Sql) Replay()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened")
            .Options;
        using var db = new ApplicationDbContext(options);

        var assembly    = db.GetService<IMigrationsAssembly>();
        var generator   = db.GetService<IMigrationsSqlGenerator>();
        var initializer = db.GetService<IModelRuntimeInitializer>();

        var dateColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // "Table.Column" with a DateTime/DateTimeOffset CLR type
        var boolColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // ditto for bool
        var clr         = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);   // every column's CLR type
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
                        foreach (var c in ct.Columns)
                        {
                            clr[$"{ct.Name}.{c.Name}"] = c.ClrType;
                            if (IsDateTime(c.ClrType)) dateColumns.Add($"{ct.Name}.{c.Name}");
                            if (IsBool(c.ClrType))     boolColumns.Add($"{ct.Name}.{c.Name}");
                        }
                        break;
                    case AddColumnOperation ac:
                        clr[$"{ac.Table}.{ac.Name}"] = ac.ClrType;
                        if (IsDateTime(ac.ClrType)) dateColumns.Add($"{ac.Table}.{ac.Name}");
                        if (IsBool(ac.ClrType))     boolColumns.Add($"{ac.Table}.{ac.Name}");
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
                // A column or table a later migration removes (e.g. RemoveUploadedCoverLetters) no longer
                // exists in production, so stop tracking it rather than reporting it as missing.
                foreach (Match m in DropColumn.Matches(sql))
                {
                    types.Remove($"{m.Groups["table"].Value}.{m.Groups["col"].Value}");
                    clr.Remove($"{m.Groups["table"].Value}.{m.Groups["col"].Value}");
                    dateColumns.Remove($"{m.Groups["table"].Value}.{m.Groups["col"].Value}");
                    boolColumns.Remove($"{m.Groups["table"].Value}.{m.Groups["col"].Value}");
                }
                foreach (Match m in DropTable.Matches(sql))
                {
                    var prefix = m.Groups["table"].Value + ".";
                    foreach (var key in types.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        types.Remove(key);
                    dateColumns.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    boolColumns.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    foreach (var key in clr.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        clr.Remove(key);
                }
            }
        }

        string Resolve(string c) => types.TryGetValue(c, out var t) ? t : "<not found in generated SQL>";

        var result = dateColumns.ToDictionary(c => c, Resolve, StringComparer.OrdinalIgnoreCase);
        var bools  = boolColumns.ToDictionary(c => c, Resolve, StringComparer.OrdinalIgnoreCase);
        var every  = clr.ToDictionary(kv => kv.Key, kv => (Clr: kv.Value, Sql: Resolve(kv.Key)), StringComparer.OrdinalIgnoreCase);
        return (result, bools, every, allSql);
    }

    [Fact]
    public void Every_DateTime_column_is_timestamptz_on_PostgreSQL()
    {
        var (types, _, _, _) = Replay();

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

        var (_, _, _, sql) = Replay();
        var fixSql = string.Join("\n", sql.Where(s => s.Contains("FixApplicationNoteCreatedAtType")));
        Assert.Contains("ALTER COLUMN \"CreatedAt\" TYPE timestamp with time zone", fixSql);
        Assert.Contains("USING COALESCE(NULLIF(btrim(\"CreatedAt\"::text), '')::timestamp with time zone, now())", fixSql);
        Assert.Contains("ALTER COLUMN \"LockoutEnd\" TYPE timestamp with time zone", fixSql);
        Assert.Contains("USING NULLIF(btrim(\"LockoutEnd\"::text), '')::timestamp with time zone", fixSql);
        Assert.Contains("ADD GENERATED BY DEFAULT AS IDENTITY", fixSql);
    }

    /// <summary>
    /// Every bool column must land as <c>boolean</c>, not the <c>INTEGER</c> the SQLite scaffold emits.
    /// int4 is not a bool to Npgsql, so a column that slips through throws on every read — which is
    /// what FixPostgreSqlTypesAndAddDataProtection had to go back and repair for eight columns. This is the check that
    /// stops the ninth, and it needs no per-table list to do it.
    /// </summary>
    [Fact]
    public void Every_bool_column_is_boolean_on_PostgreSQL()
    {
        var (_, bools, _, _) = Replay();

        // 7, not 9: CoverLetterVersions.IsActive and UserProfiles.IsPublic were on things later dropped
        // (RemoveUploadedCoverLetters, RemovePublicProfile), so the replay correctly stops tracking them.
        Assert.True(bools.Count >= 7, $"expected to track at least 7 bool columns, found {bools.Count}: {string.Join(", ", bools.Keys)}");
        Assert.Contains("ResumeVersions.IsActive", bools.Keys);
        Assert.Contains("ParsedResumes.Applied", bools.Keys);

        var wrong = bools.Where(kv => !kv.Value.Equals("boolean", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(wrong.Count == 0, "Wrong PostgreSQL type for: " + string.Join("; ", wrong.Select(kv => $"{kv.Key} = {kv.Value}")));
    }

    /// <summary>
    /// The PostgreSQL store types that are correct for each CLR type. SQLite's scaffold emits only
    /// TEXT / INTEGER / REAL / BLOB, so anything whose right-hand side isn't one of those is a column
    /// a migration has to branch on by hand (CLAUDE.md §8).
    /// </summary>
    /// <remarks>
    /// Two families were already wrong in this repo before anyone thought to look —
    /// DateTime (FixApplicationNoteCreatedAtType) and bool (AddParsedResumes, caught pre-merge) — and
    /// <c>double</c> was right only because the author of AddStatusSuggestions happened to remember.
    /// A per-family test would keep missing the family nobody thought of, so this one is driven by the
    /// table below and <b>fails on a CLR type it has never seen</b> rather than skipping it.
    /// </remarks>
    private static readonly Dictionary<Type, string[]> CorrectPostgresTypes = new()
    {
        // Wrong by default from the SQLite scaffold — each needs an ActiveProvider branch.
        [typeof(DateTime)]       = new[] { "timestamp with time zone" },
        [typeof(DateTimeOffset)] = new[] { "timestamp with time zone" },
        [typeof(bool)]           = new[] { "boolean" },
        [typeof(double)]         = new[] { "double precision" },
        [typeof(float)]          = new[] { "real" },
        [typeof(decimal)]        = new[] { "numeric" },
        [typeof(Guid)]           = new[] { "uuid" },
        [typeof(byte[])]         = new[] { "bytea" },

        // Right by accident: PostgreSQL accepts SQLite's spelling and means the same thing.
        // "character varying" is deliberately NOT allowed for string — see the test below.
        [typeof(string)] = new[] { "TEXT", "text" },
        // SERIAL is integer-plus-sequence, written by hand in FixDataProtectionKeysAutoIncrement
        // (raw SQL, PostgreSQL branch only) because that table was created without one.
        [typeof(int)]    = new[] { "INTEGER", "integer", "SERIAL" },
        [typeof(long)]   = new[] { "INTEGER", "integer", "bigint", "BIGSERIAL" },
    };

    private static Type Underlying(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    /// <summary>
    /// Every column in the final schema lands on a PostgreSQL type that means what the CLR type means.
    /// This is the general form of the DateTime and bool tests above: no per-table list, and a CLR type
    /// the table has never seen is a failure telling you to decide, not a silent pass.
    /// </summary>
    [Fact]
    public void Every_column_lands_on_a_PostgreSQL_type_that_matches_its_CLR_type()
    {
        var (_, _, every, _) = Replay();

        Assert.True(every.Count >= 60, $"expected to track at least 60 columns, found {every.Count}");

        var unmapped = every
            .Where(kv => !CorrectPostgresTypes.ContainsKey(Underlying(kv.Value.Clr)))
            .Select(kv => $"{kv.Key} is {Underlying(kv.Value.Clr).Name} -> {kv.Value.Sql}")
            .Distinct()
            .ToList();

        Assert.True(unmapped.Count == 0,
            "CLR types with no entry in CorrectPostgresTypes — add one and say what PostgreSQL should use:\n  "
            + string.Join("\n  ", unmapped));

        var wrong = every
            .Where(kv => CorrectPostgresTypes.TryGetValue(Underlying(kv.Value.Clr), out var ok)
                         && !ok.Any(t => kv.Value.Sql.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => $"{kv.Key} ({Underlying(kv.Value.Clr).Name}) = {kv.Value.Sql}")
            .ToList();

        Assert.True(wrong.Count == 0,
            "Wrong PostgreSQL type — the migration needs an ActiveProvider branch for these:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// No app column may become <c>character varying(n)</c>.
    /// </summary>
    /// <remarks>
    /// Several entities carry <c>[StringLength]</c> (ApplicationNote.Text 2000, StatusSuggestion.Summary
    /// 300, …). Those limits reach PostgreSQL only if a migration lets them: today every migration sets
    /// <c>type: "TEXT"</c> explicitly, which overrides maxLength, so the columns are <c>text</c> on both
    /// providers and the limits are enforced in C# instead. <b>That explicit type is load-bearing.</b> Drop
    /// it on a new migration and the column becomes <c>varchar(n)</c> on PostgreSQL only — where an
    /// over-long value is error 22001 in production and a silent success locally, which is precisely the
    /// shape of bug this file exists to prevent. `AspNetMigrationsHistory` is EF's own table, not ours.
    /// </remarks>
    [Fact]
    public void No_app_column_becomes_a_length_limited_varchar_on_PostgreSQL()
    {
        var (_, _, every, _) = Replay();

        var bounded = every
            .Where(kv => kv.Value.Sql.StartsWith("character varying", StringComparison.OrdinalIgnoreCase))
            .Select(kv => $"{kv.Key} = {kv.Value.Sql}")
            .ToList();

        Assert.True(bounded.Count == 0,
            "These would reject over-long values on PostgreSQL only. Set type: \"TEXT\" explicitly in the migration "
            + "and bound the value in C#:\n  " + string.Join("\n  ", bounded));
    }

    /// <summary>Tables added after FixPostgreSqlAutoIncrement must declare their own identity, or inserts fail with 23502 on Postgres.</summary>
    [Theory]
    [InlineData("GmailConnections")]
    [InlineData("StatusSuggestions")]
    [InlineData("ParsedResumes")]
    [InlineData("PracticeQuestions")]
    public void New_tables_get_an_identity_key_on_PostgreSQL(string table)
    {
        var (_, _, _, sql) = Replay();
        var create = sql.FirstOrDefault(s => s.Contains($"CREATE TABLE \"{table}\""));
        Assert.NotNull(create);
        Assert.Matches("\"Id\" (integer|INTEGER) GENERATED BY DEFAULT AS IDENTITY", create);
    }
}
