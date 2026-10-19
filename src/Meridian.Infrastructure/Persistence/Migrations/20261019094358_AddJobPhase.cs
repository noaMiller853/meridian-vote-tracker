using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phase",
                table: "background_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_background_jobs_phase",
                table: "background_jobs",
                sql: "phase IS NULL OR phase IN ('Reading', 'Classifying', 'Writing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_background_jobs_phase",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "phase",
                table: "background_jobs");
        }
    }
}
