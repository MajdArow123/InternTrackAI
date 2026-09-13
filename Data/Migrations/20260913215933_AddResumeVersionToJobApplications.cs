using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResumeVersionToJobApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Scaffolded against SQLite; TEXT / INTEGER are also valid PostgreSQL type names (text /
            // int4), so no per-provider branch is needed. No DateTime columns are added here. On SQLite
            // the foreign key triggers EF's table rebuild, which is why `database update` warns that
            // this migration can't run inside a transaction.
            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "ResumeVersions",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResumeVersionId",
                table: "JobApplications",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobApplications_ResumeVersionId",
                table: "JobApplications",
                column: "ResumeVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobApplications_ResumeVersions_ResumeVersionId",
                table: "JobApplications",
                column: "ResumeVersionId",
                principalTable: "ResumeVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobApplications_ResumeVersions_ResumeVersionId",
                table: "JobApplications");

            migrationBuilder.DropIndex(
                name: "IX_JobApplications_ResumeVersionId",
                table: "JobApplications");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "ResumeVersionId",
                table: "JobApplications");
        }
    }
}
