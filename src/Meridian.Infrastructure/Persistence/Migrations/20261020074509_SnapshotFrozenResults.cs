using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotFrozenResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The snapshot tables are empty: snapshots did not exist before this step. The new NOT NULL columns have no
            // default on purpose (a default would let an insert without a title or the frozen strings pass), so adding
            // them fails, instead of inventing values, if a table is not empty.
            // snapshot_contributions (one row per country and vote, about 1M rows per full-history snapshot) is replaced
            // by two frozen strings per country on snapshot_scores (DECISIONS §6).

            migrationBuilder.DropTable(
                name: "snapshot_contributions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_background_jobs_phase",
                table: "background_jobs");

            migrationBuilder.AddColumn<bool>(
                name: "exclude_without_position",
                table: "snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "replaced_snapshot_id",
                table: "snapshots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ordinal",
                table: "snapshot_votes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "session",
                table: "snapshot_votes",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "snapshot_votes",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "vote_date",
                table: "snapshot_votes",
                type: "date",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "absent_votes",
                table: "snapshot_scores",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "abstained_votes",
                table: "snapshot_scores",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "ballots",
                table: "snapshot_scores",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "country_name",
                table: "snapshot_scores",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "matched_votes",
                table: "snapshot_scores",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "no_ballot_votes",
                table: "snapshot_scores",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "not_a_member_votes",
                table: "snapshot_scores",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "not_matched_votes",
                table: "snapshot_scores",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "outcomes",
                table: "snapshot_scores",
                type: "text",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_replaced_snapshot_id",
                table: "snapshots",
                column: "replaced_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ux_snapshot_votes_ordinal",
                table: "snapshot_votes",
                columns: new[] { "snapshot_id", "ordinal" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_snapshot_votes_ordinal",
                table: "snapshot_votes",
                sql: "(included AND ordinal >= 0 AND reason IS NULL) OR (NOT included AND ordinal IS NULL AND reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_snapshot_scores_score",
                table: "snapshot_scores",
                sql: "score IS NULL OR score BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_snapshot_scores_strings",
                table: "snapshot_scores",
                sql: "length(ballots) = length(outcomes)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_background_jobs_phase",
                table: "background_jobs",
                sql: "phase IS NULL OR phase IN ('Reading', 'Classifying', 'Writing', 'Selecting', 'Calculating')");

            migrationBuilder.AddForeignKey(
                name: "fk_snapshots_snapshots_replaced_snapshot_id",
                table: "snapshots",
                column: "replaced_snapshot_id",
                principalTable: "snapshots",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_snapshots_snapshots_replaced_snapshot_id",
                table: "snapshots");

            migrationBuilder.DropIndex(
                name: "ix_snapshots_replaced_snapshot_id",
                table: "snapshots");

            migrationBuilder.DropIndex(
                name: "ux_snapshot_votes_ordinal",
                table: "snapshot_votes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_snapshot_votes_ordinal",
                table: "snapshot_votes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_snapshot_scores_score",
                table: "snapshot_scores");

            migrationBuilder.DropCheckConstraint(
                name: "ck_snapshot_scores_strings",
                table: "snapshot_scores");

            migrationBuilder.DropCheckConstraint(
                name: "ck_background_jobs_phase",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "exclude_without_position",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "replaced_snapshot_id",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "ordinal",
                table: "snapshot_votes");

            migrationBuilder.DropColumn(
                name: "session",
                table: "snapshot_votes");

            migrationBuilder.DropColumn(
                name: "title",
                table: "snapshot_votes");

            migrationBuilder.DropColumn(
                name: "vote_date",
                table: "snapshot_votes");

            migrationBuilder.DropColumn(
                name: "absent_votes",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "abstained_votes",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "ballots",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "country_name",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "matched_votes",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "no_ballot_votes",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "not_a_member_votes",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "not_matched_votes",
                table: "snapshot_scores");

            migrationBuilder.DropColumn(
                name: "outcomes",
                table: "snapshot_scores");

            migrationBuilder.CreateTable(
                name: "snapshot_contributions",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    ballot = table.Column<string>(type: "text", nullable: true),
                    counted = table.Column<bool>(type: "boolean", nullable: false),
                    matched = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_contributions", x => new { x.snapshot_id, x.country_code, x.vote_id });
                    table.CheckConstraint("ck_snapshot_contributions_ballot", "ballot IS NULL OR ballot IN ('Y', 'N', 'A', 'X')");
                    table.CheckConstraint("ck_snapshot_contributions_weight", "weight BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_snapshot_contributions_countries_country_code",
                        column: x => x.country_code,
                        principalTable: "countries",
                        principalColumn: "country_code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshot_contributions_snapshot_votes_snapshot_id_vote_id",
                        columns: x => new { x.snapshot_id, x.vote_id },
                        principalTable: "snapshot_votes",
                        principalColumns: new[] { "snapshot_id", "vote_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshot_contributions_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshot_contributions_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_background_jobs_phase",
                table: "background_jobs",
                sql: "phase IS NULL OR phase IN ('Reading', 'Classifying', 'Writing')");

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_contributions_country_code",
                table: "snapshot_contributions",
                column: "country_code");

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_contributions_snapshot_id_vote_id",
                table: "snapshot_contributions",
                columns: new[] { "snapshot_id", "vote_id" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_contributions_vote_id",
                table: "snapshot_contributions",
                column: "vote_id");
        }
    }
}
