using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <summary>
    /// Replaces <c>InterviewPrepSessions</c> (one JSON blob of questions per application) with
    /// <c>PracticeQuestions</c> (one row per question, deduped by a unique hash).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The old table is dropped, not migrated.</b> Its rows are regenerable AI output that the app
    /// already overwrote on every regenerate, and converting the blobs would mean parsing JSON inside a
    /// migration — the kind of thing that works on SQLite and fails on PostgreSQL. Users press
    /// Regenerate and get a fresh set. Agreed with the maintainer before writing this.
    /// </para>
    /// <para>
    /// Needs all three CLAUDE.md §8 provider branches at once, which is why it is the template to copy:
    /// DateTime (TEXT would be a text column on PostgreSQL and throw on every read), bool (INTEGER is
    /// int4 and Npgsql refuses to read it as a bool), and a new identity key (without the annotation
    /// PostgreSQL inserts fail with 23502).
    /// </para>
    /// </remarks>
    public partial class AddPracticeQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isPostgres = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";
            var dateTime = isPostgres ? "timestamp with time zone" : "TEXT";
            var boolean  = isPostgres ? "boolean" : "INTEGER";

            migrationBuilder.DropTable(
                name: "InterviewPrepSessions");

            migrationBuilder.CreateTable(
                name: "PracticeQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Difficulty = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", nullable: false),
                    PromptHash = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelHint = table.Column<string>(type: "TEXT", nullable: true),
                    UserAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    AiFeedback = table.Column<string>(type: "TEXT", nullable: true),
                    Score = table.Column<int>(type: "INTEGER", nullable: true),
                    AnsweredAt = table.Column<DateTime>(type: dateTime, nullable: true),
                    IsSaved = table.Column<bool>(type: boolean, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: dateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PracticeQuestions_JobApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "JobApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeQuestions_ApplicationId",
                table: "PracticeQuestions",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeQuestions_UserId_CreatedAt",
                table: "PracticeQuestions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeQuestions_UserId_PromptHash",
                table: "PracticeQuestions",
                columns: new[] { "UserId", "PromptHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PracticeQuestions_UserId_Topic",
                table: "PracticeQuestions",
                columns: new[] { "UserId", "Topic" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PracticeQuestions");

            migrationBuilder.CreateTable(
                name: "InterviewPrepSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobApplicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QuestionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterviewPrepSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InterviewPrepSessions_JobApplications_JobApplicationId",
                        column: x => x.JobApplicationId,
                        principalTable: "JobApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InterviewPrepSessions_JobApplicationId",
                table: "InterviewPrepSessions",
                column: "JobApplicationId");
        }
    }
}
