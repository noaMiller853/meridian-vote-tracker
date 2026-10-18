using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meridian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    done = table.Column<int>(type: "integer", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_jobs", x => x.id);
                    table.CheckConstraint("ck_background_jobs_status", "status IN ('Queued', 'Running', 'Succeeded', 'Failed')");
                    table.CheckConstraint("ck_background_jobs_type", "type IN ('Import', 'Snapshot')");
                });

            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    country_code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_countries", x => x.country_code);
                    table.CheckConstraint("ck_countries_country_code", "country_code ~ '^[A-Z]{3}$'");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "role IN ('Analyst', 'Admin')");
                });

            migrationBuilder.CreateTable(
                name: "country_memberships",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    member_from = table.Column<DateOnly>(type: "date", nullable: false),
                    member_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_country_memberships", x => x.id);
                    table.CheckConstraint("ck_country_memberships_period", "member_to IS NULL OR member_to >= member_from");
                    table.ForeignKey(
                        name: "fk_country_memberships_countries_country_code",
                        column: x => x.country_code,
                        principalTable: "countries",
                        principalColumn: "country_code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "imports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    storage_uri = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    uploaded_by_id = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    background_job_id = table.Column<long>(type: "bigint", nullable: true),
                    new_count = table.Column<int>(type: "integer", nullable: false),
                    unchanged_count = table.Column<int>(type: "integer", nullable: false),
                    duplicate_count = table.Column<int>(type: "integer", nullable: false),
                    invalid_count = table.Column<int>(type: "integer", nullable: false),
                    contradiction_count = table.Column<int>(type: "integer", nullable: false),
                    conflict_count = table.Column<int>(type: "integer", nullable: false),
                    normalized_count = table.Column<int>(type: "integer", nullable: false),
                    report = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imports", x => x.id);
                    table.CheckConstraint("ck_imports_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_imports_background_jobs_background_job_id",
                        column: x => x.background_job_id,
                        principalTable: "background_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_imports_users_uploaded_by_id",
                        column: x => x.uploaded_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    filter = table.Column<string>(type: "jsonb", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    quarter = table.Column<string>(type: "text", nullable: true),
                    created_by_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    background_job_id = table.Column<long>(type: "bigint", nullable: true),
                    published_by_id = table.Column<long>(type: "bigint", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshots", x => x.id);
                    table.CheckConstraint("ck_snapshots_published", "status <> 'Published' OR (quarter IS NOT NULL AND published_by_id IS NOT NULL AND published_at IS NOT NULL)");
                    table.CheckConstraint("ck_snapshots_quarter_format", "quarter IS NULL OR quarter ~ '^[0-9]{4}-Q[1-4]$'");
                    table.CheckConstraint("ck_snapshots_status", "status IN ('Calculating', 'Saved', 'Failed', 'Published')");
                    table.ForeignKey(
                        name: "fk_snapshots_background_jobs_background_job_id",
                        column: x => x.background_job_id,
                        principalTable: "background_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshots_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshots_users_published_by_id",
                        column: x => x.published_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_rows",
                columns: table => new
                {
                    import_id = table.Column<long>(type: "bigint", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    raw_line = table.Column<string>(type: "text", nullable: false),
                    assembly_vote_id = table.Column<string>(type: "text", nullable: true),
                    session = table.Column<string>(type: "text", nullable: true),
                    vote_date = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    country_code = table.Column<string>(type: "text", nullable: true),
                    original_country_code = table.Column<string>(type: "text", nullable: true),
                    country_name = table.Column<string>(type: "text", nullable: true),
                    ballot = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<string>(type: "text", nullable: true),
                    normalized = table.Column<bool>(type: "boolean", nullable: false),
                    classification = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    original_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_rows", x => new { x.import_id, x.row_number });
                    table.CheckConstraint("ck_import_rows_classification", "classification IN ('New', 'Unchanged', 'Duplicate', 'Invalid', 'Contradiction', 'Conflict')");
                    table.ForeignKey(
                        name: "fk_import_rows_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "votes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    assembly_vote_id = table.Column<string>(type: "text", nullable: false),
                    session = table.Column<int>(type: "integer", nullable: false),
                    vote_date = table.Column<DateOnly>(type: "date", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    first_import_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_votes", x => x.id);
                    table.ForeignKey(
                        name: "fk_votes_imports_first_import_id",
                        column: x => x.first_import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_scores",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    rank = table.Column<int>(type: "integer", nullable: true),
                    counted_weight = table.Column<int>(type: "integer", nullable: false),
                    matched_weight = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_scores", x => new { x.snapshot_id, x.country_code });
                    table.CheckConstraint("ck_snapshot_scores_rank", "(score IS NULL) = (rank IS NULL)");
                    table.ForeignKey(
                        name: "fk_snapshot_scores_countries_country_code",
                        column: x => x.country_code,
                        principalTable: "countries",
                        principalColumn: "country_code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshot_scores_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    vote_id = table.Column<long>(type: "bigint", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    old_value = table.Column<string>(type: "jsonb", nullable: true),
                    new_value = table.Column<string>(type: "jsonb", nullable: true),
                    actor_id = table.Column<long>(type: "bigint", nullable: false),
                    import_id = table.Column<long>(type: "bigint", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_events_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_events_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_events_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conflicts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: true),
                    field_name = table.Column<string>(type: "text", nullable: true),
                    approved_value = table.Column<string>(type: "text", nullable: false),
                    proposed_value = table.Column<string>(type: "text", nullable: false),
                    approved_source_import_id = table.Column<long>(type: "bigint", nullable: false),
                    proposed_source_import_id = table.Column<long>(type: "bigint", nullable: false),
                    proposed_source_row_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    decided_by_id = table.Column<long>(type: "bigint", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflicts", x => x.id);
                    table.CheckConstraint("ck_conflicts_ballot_values", "kind <> 'CountryVote' OR (approved_value IN ('Y', 'N', 'A', 'X') AND proposed_value IN ('Y', 'N', 'A', 'X'))");
                    table.CheckConstraint("ck_conflicts_decision", "(status = 'Pending') = (decided_by_id IS NULL AND decided_at IS NULL)");
                    table.CheckConstraint("ck_conflicts_kind", "kind IN ('CountryVote', 'VoteField')");
                    table.CheckConstraint("ck_conflicts_status", "status IN ('Pending', 'Approved', 'Rejected')");
                    table.CheckConstraint("ck_conflicts_target", "(kind = 'CountryVote' AND country_code IS NOT NULL AND field_name IS NULL) OR (kind = 'VoteField' AND country_code IS NULL AND field_name IN ('session', 'vote_date', 'title'))");
                    table.ForeignKey(
                        name: "fk_conflicts_countries_country_code",
                        column: x => x.country_code,
                        principalTable: "countries",
                        principalColumn: "country_code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_import_rows_proposed_source_import_id_proposed_so",
                        columns: x => new { x.proposed_source_import_id, x.proposed_source_row_number },
                        principalTable: "import_rows",
                        principalColumns: new[] { "import_id", "row_number" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_imports_approved_source_import_id",
                        column: x => x.approved_source_import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_users_decided_by_id",
                        column: x => x.decided_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "country_votes",
                columns: table => new
                {
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    ballot = table.Column<string>(type: "text", nullable: false),
                    source_import_id = table.Column<long>(type: "bigint", nullable: false),
                    source_row_number = table.Column<int>(type: "integer", nullable: false),
                    approved_by_id = table.Column<long>(type: "bigint", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_country_votes", x => new { x.vote_id, x.country_code });
                    table.CheckConstraint("ck_country_votes_ballot", "ballot IN ('Y', 'N', 'A', 'X')");
                    table.ForeignKey(
                        name: "fk_country_votes_countries_country_code",
                        column: x => x.country_code,
                        principalTable: "countries",
                        principalColumn: "country_code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_country_votes_import_rows_source_import_id_source_row_number",
                        columns: x => new { x.source_import_id, x.source_row_number },
                        principalTable: "import_rows",
                        principalColumns: new[] { "import_id", "row_number" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_country_votes_users_approved_by_id",
                        column: x => x.approved_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_country_votes_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_votes",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    assembly_vote_id = table.Column<string>(type: "text", nullable: false),
                    desired_outcome = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<short>(type: "smallint", nullable: true),
                    included = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_votes", x => new { x.snapshot_id, x.vote_id });
                    table.CheckConstraint("ck_snapshot_votes_desired_outcome", "desired_outcome IS NULL OR desired_outcome IN ('Y', 'N')");
                    table.CheckConstraint("ck_snapshot_votes_included", "NOT included OR (desired_outcome IS NOT NULL AND weight IS NOT NULL)");
                    table.CheckConstraint("ck_snapshot_votes_weight", "weight IS NULL OR weight BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_snapshot_votes_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshot_votes_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vote_positions",
                columns: table => new
                {
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    desired_outcome = table.Column<string>(type: "text", nullable: false),
                    weight = table.Column<short>(type: "smallint", nullable: false),
                    set_by_id = table.Column<long>(type: "bigint", nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vote_positions", x => x.vote_id);
                    table.CheckConstraint("ck_vote_positions_desired_outcome", "desired_outcome IN ('Y', 'N')");
                    table.CheckConstraint("ck_vote_positions_weight", "weight BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_vote_positions_users_set_by_id",
                        column: x => x.set_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vote_positions_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_contributions",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    ballot = table.Column<string>(type: "text", nullable: true),
                    counted = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<short>(type: "smallint", nullable: false),
                    matched = table.Column<bool>(type: "boolean", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_id",
                table: "audit_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_import_id",
                table: "audit_events",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_vote_id_occurred_at",
                table: "audit_events",
                columns: new[] { "vote_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_approved_source_import_id",
                table: "conflicts",
                column: "approved_source_import_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_country_code",
                table: "conflicts",
                column: "country_code");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_decided_by_id",
                table: "conflicts",
                column: "decided_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_proposed_source_import_id_proposed_source_row_num",
                table: "conflicts",
                columns: new[] { "proposed_source_import_id", "proposed_source_row_number" });

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_status",
                table: "conflicts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_conflicts_pending_target",
                table: "conflicts",
                columns: new[] { "vote_id", "kind", "country_code", "field_name", "proposed_value" },
                unique: true,
                filter: "status = 'Pending'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_country_memberships_country_code",
                table: "country_memberships",
                column: "country_code");

            migrationBuilder.CreateIndex(
                name: "ix_country_votes_approved_by_id",
                table: "country_votes",
                column: "approved_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_country_votes_country_code",
                table: "country_votes",
                column: "country_code");

            migrationBuilder.CreateIndex(
                name: "ix_country_votes_source_import_id_source_row_number",
                table: "country_votes",
                columns: new[] { "source_import_id", "source_row_number" });

            migrationBuilder.CreateIndex(
                name: "ix_imports_background_job_id",
                table: "imports",
                column: "background_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_imports_sha256",
                table: "imports",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_imports_uploaded_by_id",
                table: "imports",
                column: "uploaded_by_id");

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

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_scores_country_code",
                table: "snapshot_scores",
                column: "country_code");

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_votes_vote_id",
                table: "snapshot_votes",
                column: "vote_id");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_background_job_id",
                table: "snapshots",
                column: "background_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_created_by_id",
                table: "snapshots",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_published_by_id",
                table: "snapshots",
                column: "published_by_id");

            migrationBuilder.CreateIndex(
                name: "ux_snapshots_published_quarter",
                table: "snapshots",
                column: "quarter",
                unique: true,
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_users_name",
                table: "users",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vote_positions_set_by_id",
                table: "vote_positions",
                column: "set_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_votes_first_import_id",
                table: "votes",
                column: "first_import_id");

            migrationBuilder.CreateIndex(
                name: "ux_votes_assembly_vote_id",
                table: "votes",
                column: "assembly_vote_id",
                unique: true);

            // Not expressible in the EF model: membership periods of one country never overlap.
            // A null member_to is an open upper bound; '[]' makes both ends inclusive.
            migrationBuilder.Sql("""
                ALTER TABLE country_memberships
                    ADD CONSTRAINT ex_country_memberships_no_overlap
                    EXCLUDE USING gist (country_code WITH =, daterange(member_from, member_to, '[]') WITH &&);
                """);

            // The seed load and other automatic work are recorded as actions by this user.
            migrationBuilder.Sql("INSERT INTO users (name, role) VALUES ('system', 'Admin');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "conflicts");

            migrationBuilder.DropTable(
                name: "country_memberships");

            migrationBuilder.DropTable(
                name: "country_votes");

            migrationBuilder.DropTable(
                name: "snapshot_contributions");

            migrationBuilder.DropTable(
                name: "snapshot_scores");

            migrationBuilder.DropTable(
                name: "vote_positions");

            migrationBuilder.DropTable(
                name: "import_rows");

            migrationBuilder.DropTable(
                name: "snapshot_votes");

            migrationBuilder.DropTable(
                name: "countries");

            migrationBuilder.DropTable(
                name: "snapshots");

            migrationBuilder.DropTable(
                name: "votes");

            migrationBuilder.DropTable(
                name: "imports");

            migrationBuilder.DropTable(
                name: "background_jobs");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
