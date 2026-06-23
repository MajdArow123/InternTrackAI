using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixPublicProfileIsPublicTypeForPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // UserProfiles.IsPublic was added via AddColumn<bool>(type: "INTEGER", ...), which is
            // correct for SQLite but creates an int4 column on PostgreSQL, where bool comparisons
            // require a native boolean column.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // The column's existing integer DEFAULT can't be auto-cast to boolean, so it
                // has to be dropped before the type change and re-added afterward.
                migrationBuilder.Sql("""ALTER TABLE "UserProfiles" ALTER COLUMN "IsPublic" DROP DEFAULT;""");
                migrationBuilder.Sql("""ALTER TABLE "UserProfiles" ALTER COLUMN "IsPublic" TYPE boolean USING "IsPublic"::boolean;""");
                migrationBuilder.Sql("""ALTER TABLE "UserProfiles" ALTER COLUMN "IsPublic" SET DEFAULT false;""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""ALTER TABLE "UserProfiles" ALTER COLUMN "IsPublic" DROP DEFAULT;""");
                migrationBuilder.Sql("""ALTER TABLE "UserProfiles" ALTER COLUMN "IsPublic" TYPE integer USING "IsPublic"::integer;""");
                migrationBuilder.Sql("""ALTER TABLE "UserProfiles" ALTER COLUMN "IsPublic" SET DEFAULT 0;""");
            }
        }
    }
}
