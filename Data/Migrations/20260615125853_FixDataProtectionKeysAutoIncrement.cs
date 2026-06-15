using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixDataProtectionKeysAutoIncrement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The DataProtectionKeys table was created with type "INTEGER" from a SQLite-generated migration,
            // which on PostgreSQL creates a plain integer column with no sequence/auto-increment.
            // Drop and recreate with SERIAL so Npgsql can write keys.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""DROP TABLE IF EXISTS "DataProtectionKeys";""");
                migrationBuilder.Sql("""
                    CREATE TABLE "DataProtectionKeys" (
                        "Id" SERIAL NOT NULL,
                        "FriendlyName" text,
                        "Xml" text,
                        CONSTRAINT "PK_DataProtectionKeys" PRIMARY KEY ("Id")
                    );
                """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
