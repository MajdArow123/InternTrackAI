using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Same provider branching as AddGmailConnections: timestamptz DateTime columns and an
            // identity key on PostgreSQL, TEXT + autoincrement on SQLite.
            var dateTime = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "timestamp with time zone"
                : "TEXT";
            // An explicit REAL would be float4 on PostgreSQL, which Npgsql refuses to read as a double.
            var dbl = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "double precision"
                : "REAL";

            migrationBuilder.CreateTable(
                name: "StatusSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApplicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    GmailMessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SuggestedStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: dbl, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    InterviewAt = table.Column<DateTime>(type: dateTime, nullable: true),
                    EmailSubject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EmailFrom = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    EmailDate = table.Column<DateTime>(type: dateTime, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: dateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusSuggestions_JobApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "JobApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatusSuggestions_ApplicationId",
                table: "StatusSuggestions",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusSuggestions_UserId_GmailMessageId",
                table: "StatusSuggestions",
                columns: new[] { "UserId", "GmailMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusSuggestions_UserId_Status",
                table: "StatusSuggestions",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatusSuggestions");
        }
    }
}
