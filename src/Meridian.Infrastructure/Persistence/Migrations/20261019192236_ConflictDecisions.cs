using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConflictDecisions : Migration
    {
        private static readonly string[] SourceColumns =
            ["session_source_import_id", "vote_date_source_import_id", "title_source_import_id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // votes.source_import_id becomes one source per field (CON-02: an approved title must not move the source
            // of the date). Added nullable, backfilled from source_import_id, then made NOT NULL, so existing votes stay
            // valid (a NOT NULL DEFAULT 0 would break the foreign keys).
            foreach (var column in SourceColumns)
                migrationBuilder.AddColumn<long>(name: column, table: "votes", type: "bigint", nullable: true);

            migrationBuilder.Sql("""
                UPDATE votes
                SET session_source_import_id = source_import_id,
                    vote_date_source_import_id = source_import_id,
                    title_source_import_id = source_import_id;
                """);

            foreach (var column in SourceColumns)
            {
                migrationBuilder.AlterColumn<long>(name: column, table: "votes", type: "bigint", nullable: false,
                    oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
                migrationBuilder.CreateIndex(name: $"ix_votes_{column}", table: "votes", column: column);
                migrationBuilder.AddForeignKey(name: $"fk_votes_imports_{column}", table: "votes", column: column,
                    principalTable: "imports", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            }

            migrationBuilder.DropForeignKey(name: "fk_votes_imports_source_import_id", table: "votes");
            migrationBuilder.DropIndex(name: "ix_votes_source_import_id", table: "votes");
            migrationBuilder.DropColumn(name: "source_import_id", table: "votes");

            // What an approval replaced (set only when Approved). No conflict can be Approved before this migration
            // (there was no decision endpoint), so the check holds without a backfill.
            migrationBuilder.AddColumn<string>(name: "replaced_value", table: "conflicts", type: "text", nullable: true);
            migrationBuilder.AddColumn<long>(name: "replaced_source_import_id", table: "conflicts", type: "bigint", nullable: true);
            migrationBuilder.CreateIndex(name: "ix_conflicts_replaced_source_import_id", table: "conflicts",
                column: "replaced_source_import_id");
            migrationBuilder.AddForeignKey(name: "fk_conflicts_imports_replaced_source_import_id", table: "conflicts",
                column: "replaced_source_import_id", principalTable: "imports", principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddCheckConstraint(name: "ck_conflicts_replaced", table: "conflicts",
                sql: "(status = 'Approved') = (replaced_value IS NOT NULL) AND (replaced_value IS NULL) = (replaced_source_import_id IS NULL)");

            // The new conflict action (ING-12).
            migrationBuilder.DropCheckConstraint(name: "ck_import_rows_conflict_action", table: "import_rows");
            migrationBuilder.AddCheckConstraint(name: "ck_import_rows_conflict_action", table: "import_rows",
                sql: "conflict_action IS NULL OR conflict_action IN ('Open', 'AlreadyPending', 'AlreadyRejected', 'AlreadySuperseded')");
            migrationBuilder.AddColumn<int>(name: "conflicts_already_superseded", table: "import_votes", type: "integer",
                nullable: false, defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "conflicts_already_superseded", table: "import_votes");
            migrationBuilder.DropCheckConstraint(name: "ck_import_rows_conflict_action", table: "import_rows");
            migrationBuilder.AddCheckConstraint(name: "ck_import_rows_conflict_action", table: "import_rows",
                sql: "conflict_action IS NULL OR conflict_action IN ('Open', 'AlreadyPending', 'AlreadyRejected')");

            migrationBuilder.DropCheckConstraint(name: "ck_conflicts_replaced", table: "conflicts");
            migrationBuilder.DropForeignKey(name: "fk_conflicts_imports_replaced_source_import_id", table: "conflicts");
            migrationBuilder.DropIndex(name: "ix_conflicts_replaced_source_import_id", table: "conflicts");
            migrationBuilder.DropColumn(name: "replaced_source_import_id", table: "conflicts");
            migrationBuilder.DropColumn(name: "replaced_value", table: "conflicts");

            // One source for all details again: the latest of the three is the closest to the old meaning
            // ("the import that last set the details").
            migrationBuilder.AddColumn<long>(name: "source_import_id", table: "votes", type: "bigint", nullable: true);
            migrationBuilder.Sql("""
                UPDATE votes
                SET source_import_id = GREATEST(session_source_import_id, vote_date_source_import_id, title_source_import_id);
                """);
            migrationBuilder.AlterColumn<long>(name: "source_import_id", table: "votes", type: "bigint", nullable: false,
                oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
            migrationBuilder.CreateIndex(name: "ix_votes_source_import_id", table: "votes", column: "source_import_id");
            migrationBuilder.AddForeignKey(name: "fk_votes_imports_source_import_id", table: "votes", column: "source_import_id",
                principalTable: "imports", principalColumn: "id", onDelete: ReferentialAction.Restrict);

            foreach (var column in SourceColumns)
            {
                migrationBuilder.DropForeignKey(name: $"fk_votes_imports_{column}", table: "votes");
                migrationBuilder.DropIndex(name: $"ix_votes_{column}", table: "votes");
                migrationBuilder.DropColumn(name: column, table: "votes");
            }
        }
    }
}
