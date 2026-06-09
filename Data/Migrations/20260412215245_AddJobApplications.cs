using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RoleTitle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    JobLink = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    WorkMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Deadline = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DateApplied = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Salary = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    JobDescription = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobApplications", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobApplications");
        }
    }
}
