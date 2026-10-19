using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added nullable, backfilled from first_import_id, then made NOT NULL, so existing votes stay valid
            // (a NOT NULL DEFAULT 0 would break the foreign key below).
            migrationBuilder.AddColumn<long>(
                name: "source_import_id",
                table: "votes",
                type: "bigint",
                nullable: true);
            migrationBuilder.Sql("UPDATE votes SET source_import_id = first_import_id;");
            migrationBuilder.AlterColumn<long>(
                name: "source_import_id",
                table: "votes",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "conflict_action",
                table: "import_rows",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "field_count",
                table: "import_rows",
                type: "integer",
                nullable: false,
                defaultValue: 8); // existing rows (none so far) were full lines

            migrationBuilder.AddColumn<string>(
                name: "normalizations",
                table: "import_rows",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "problems",
                table: "import_rows",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_votes",
                columns: table => new
                {
                    import_id = table.Column<long>(type: "bigint", nullable: false),
                    assembly_vote_id = table.Column<string>(type: "text", nullable: false),
                    vote_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    vote_created = table.Column<bool>(type: "boolean", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    new_count = table.Column<int>(type: "integer", nullable: false),
                    unchanged_count = table.Column<int>(type: "integer", nullable: false),
                    duplicate_count = table.Column<int>(type: "integer", nullable: false),
                    invalid_count = table.Column<int>(type: "integer", nullable: false),
                    contradiction_count = table.Column<int>(type: "integer", nullable: false),
                    conflict_count = table.Column<int>(type: "integer", nullable: false),
                    conflicts_opened = table.Column<int>(type: "integer", nullable: false),
                    conflicts_already_pending = table.Column<int>(type: "integer", nullable: false),
                    conflicts_already_rejected = table.Column<int>(type: "integer", nullable: false),
                    field_conflicts = table.Column<string>(type: "jsonb", nullable: true),
                    contradiction_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_votes", x => new { x.import_id, x.assembly_vote_id });
                    table.CheckConstraint("ck_import_votes_status", "status IN ('New', 'Existing', 'Contradiction', 'NoValidDetails')");
                    table.ForeignKey(
                        name: "fk_import_votes_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_import_votes_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_votes_source_import_id",
                table: "votes",
                column: "source_import_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_import_class",
                table: "import_rows",
                columns: new[] { "import_id", "classification", "row_number" });

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_import_vote",
                table: "import_rows",
                columns: new[] { "import_id", "assembly_vote_id", "row_number" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_import_rows_conflict_action",
                table: "import_rows",
                sql: "conflict_action IS NULL OR conflict_action IN ('Open', 'AlreadyPending', 'AlreadyRejected')");

            migrationBuilder.CreateIndex(
                name: "ix_import_votes_vote_id",
                table: "import_votes",
                column: "vote_id");

            migrationBuilder.AddForeignKey(
                name: "fk_votes_imports_source_import_id",
                table: "votes",
                column: "source_import_id",
                principalTable: "imports",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_votes_imports_source_import_id",
                table: "votes");

            migrationBuilder.DropTable(
                name: "import_votes");

            migrationBuilder.DropIndex(
                name: "ix_votes_source_import_id",
                table: "votes");

            migrationBuilder.DropIndex(
                name: "ix_import_rows_import_class",
                table: "import_rows");

            migrationBuilder.DropIndex(
                name: "ix_import_rows_import_vote",
                table: "import_rows");

            migrationBuilder.DropCheckConstraint(
                name: "ck_import_rows_conflict_action",
                table: "import_rows");

            migrationBuilder.DropColumn(
                name: "source_import_id",
                table: "votes");

            migrationBuilder.DropColumn(
                name: "conflict_action",
                table: "import_rows");

            migrationBuilder.DropColumn(
                name: "field_count",
                table: "import_rows");

            migrationBuilder.DropColumn(
                name: "normalizations",
                table: "import_rows");

            migrationBuilder.DropColumn(
                name: "problems",
                table: "import_rows");
        }
    }
}
