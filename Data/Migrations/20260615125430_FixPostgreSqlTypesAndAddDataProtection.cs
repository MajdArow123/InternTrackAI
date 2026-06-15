using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixPostgreSqlTypesAndAddDataProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    Xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            // Fix column types that were generated for SQLite but are wrong for PostgreSQL.
            // SQLite uses INTEGER for bool and TEXT for DateTime; PostgreSQL needs boolean and timestamptz.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // AspNetUsers — Identity boolean flags
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "EmailConfirmed" TYPE boolean USING "EmailConfirmed"::boolean;""");
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "PhoneNumberConfirmed" TYPE boolean USING "PhoneNumberConfirmed"::boolean;""");
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "TwoFactorEnabled" TYPE boolean USING "TwoFactorEnabled"::boolean;""");
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "LockoutEnabled" TYPE boolean USING "LockoutEnabled"::boolean;""");

                // ResumeVersions
                migrationBuilder.Sql("""ALTER TABLE "ResumeVersions" ALTER COLUMN "IsActive" TYPE boolean USING "IsActive"::boolean;""");
                migrationBuilder.Sql("""ALTER TABLE "ResumeVersions" ALTER COLUMN "UploadedAt" TYPE timestamp with time zone USING "UploadedAt"::timestamp with time zone;""");

                // CoverLetterVersions
                migrationBuilder.Sql("""ALTER TABLE "CoverLetterVersions" ALTER COLUMN "IsActive" TYPE boolean USING "IsActive"::boolean;""");
                migrationBuilder.Sql("""ALTER TABLE "CoverLetterVersions" ALTER COLUMN "UploadedAt" TYPE timestamp with time zone USING "UploadedAt"::timestamp with time zone;""");

                // GeneratedCoverLetters
                migrationBuilder.Sql("""ALTER TABLE "GeneratedCoverLetters" ALTER COLUMN "IsActive" TYPE boolean USING "IsActive"::boolean;""");
                migrationBuilder.Sql("""ALTER TABLE "GeneratedCoverLetters" ALTER COLUMN "GeneratedAt" TYPE timestamp with time zone USING "GeneratedAt"::timestamp with time zone;""");

                // InterviewPrepSessions
                migrationBuilder.Sql("""ALTER TABLE "InterviewPrepSessions" ALTER COLUMN "GeneratedAt" TYPE timestamp with time zone USING "GeneratedAt"::timestamp with time zone;""");

                // JobApplications — nullable DateTime columns
                migrationBuilder.Sql("""ALTER TABLE "JobApplications" ALTER COLUMN "Deadline" TYPE timestamp with time zone USING "Deadline"::timestamp with time zone;""");
                migrationBuilder.Sql("""ALTER TABLE "JobApplications" ALTER COLUMN "DateApplied" TYPE timestamp with time zone USING "DateApplied"::timestamp with time zone;""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "EmailConfirmed" TYPE integer USING "EmailConfirmed"::integer;""");
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "PhoneNumberConfirmed" TYPE integer USING "PhoneNumberConfirmed"::integer;""");
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "TwoFactorEnabled" TYPE integer USING "TwoFactorEnabled"::integer;""");
                migrationBuilder.Sql("""ALTER TABLE "AspNetUsers" ALTER COLUMN "LockoutEnabled" TYPE integer USING "LockoutEnabled"::integer;""");

                migrationBuilder.Sql("""ALTER TABLE "ResumeVersions" ALTER COLUMN "IsActive" TYPE integer USING "IsActive"::integer;""");
                migrationBuilder.Sql("""ALTER TABLE "ResumeVersions" ALTER COLUMN "UploadedAt" TYPE text USING "UploadedAt"::text;""");

                migrationBuilder.Sql("""ALTER TABLE "CoverLetterVersions" ALTER COLUMN "IsActive" TYPE integer USING "IsActive"::integer;""");
                migrationBuilder.Sql("""ALTER TABLE "CoverLetterVersions" ALTER COLUMN "UploadedAt" TYPE text USING "UploadedAt"::text;""");

                migrationBuilder.Sql("""ALTER TABLE "GeneratedCoverLetters" ALTER COLUMN "IsActive" TYPE integer USING "IsActive"::integer;""");
                migrationBuilder.Sql("""ALTER TABLE "GeneratedCoverLetters" ALTER COLUMN "GeneratedAt" TYPE text USING "GeneratedAt"::text;""");

                migrationBuilder.Sql("""ALTER TABLE "InterviewPrepSessions" ALTER COLUMN "GeneratedAt" TYPE text USING "GeneratedAt"::text;""");

                migrationBuilder.Sql("""ALTER TABLE "JobApplications" ALTER COLUMN "Deadline" TYPE text USING "Deadline"::text;""");
                migrationBuilder.Sql("""ALTER TABLE "JobApplications" ALTER COLUMN "DateApplied" TYPE text USING "DateApplied"::text;""");
            }
        }
    }
}
