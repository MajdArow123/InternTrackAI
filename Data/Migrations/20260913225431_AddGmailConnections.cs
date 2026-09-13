using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGmailConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Scaffolded against SQLite but also applied to PostgreSQL in production: DateTime
            // columns must be timestamptz there (TEXT would make every read throw), and the key
            // needs an identity, which the SQLite autoincrement annotation alone does not give it.
            var dateTime = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "timestamp with time zone"
                : "TEXT";

            migrationBuilder.CreateTable(
                name: "GmailConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    GmailAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", nullable: false),
                    TokenExpiresAt = table.Column<DateTime>(type: dateTime, nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: dateTime, nullable: true),
                    LastHistoryId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: dateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GmailConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GmailConnections_UserId",
                table: "GmailConnections",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GmailConnections");
        }
    }
}
