using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_imports_sha256",
                table: "imports");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "imports",
                type: "text",
                nullable: false,
                defaultValue: "Votes");

            migrationBuilder.CreateIndex(
                name: "ix_imports_kind_sha256",
                table: "imports",
                columns: new[] { "kind", "sha256" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_imports_kind",
                table: "imports",
                sql: "kind IN ('Votes', 'MemberStates')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_imports_kind_sha256",
                table: "imports");

            migrationBuilder.DropCheckConstraint(
                name: "ck_imports_kind",
                table: "imports");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "imports");

            migrationBuilder.CreateIndex(
                name: "ix_imports_sha256",
                table: "imports",
                column: "sha256");
        }
    }
}
