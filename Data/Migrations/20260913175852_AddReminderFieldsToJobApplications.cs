using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderFieldsToJobApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Scaffolded against SQLite (TEXT for DateTime) but also applied to PostgreSQL in
            // production, where a DateTime must be timestamptz — pick the type per provider up front
            // instead of needing a follow-up "fix types" migration.
            var dateTime = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "timestamp with time zone"
                : "TEXT";

            // Existing users get the documented default (7 days), not 0.
            migrationBuilder.AddColumn<int>(
                name: "FollowUpAfterDays",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpAt",
                table: "JobApplications",
                type: dateTime,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InterviewAt",
                table: "JobApplications",
                type: dateTime,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastContactAt",
                table: "JobApplications",
                type: dateTime,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FollowUpAfterDays",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "FollowUpAt",
                table: "JobApplications");

            migrationBuilder.DropColumn(
                name: "InterviewAt",
                table: "JobApplications");

            migrationBuilder.DropColumn(
                name: "LastContactAt",
                table: "JobApplications");
        }
    }
}
