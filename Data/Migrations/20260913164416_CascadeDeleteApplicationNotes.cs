using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class CascadeDeleteApplicationNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Notes were previously written without a foreign key, so rows whose application has
            // already been deleted may exist. Remove them first or the constraint below fails
            // (PostgreSQL validates existing rows; SQLite rebuilds the table). Double-quoted
            // identifiers are valid on both providers.
            migrationBuilder.Sql("""
                DELETE FROM "ApplicationNotes"
                WHERE "JobApplicationId" NOT IN (SELECT "Id" FROM "JobApplications");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationNotes_JobApplicationId",
                table: "ApplicationNotes",
                column: "JobApplicationId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationNotes_JobApplications_JobApplicationId",
                table: "ApplicationNotes",
                column: "JobApplicationId",
                principalTable: "JobApplications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationNotes_JobApplications_JobApplicationId",
                table: "ApplicationNotes");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationNotes_JobApplicationId",
                table: "ApplicationNotes");
        }
    }
}
