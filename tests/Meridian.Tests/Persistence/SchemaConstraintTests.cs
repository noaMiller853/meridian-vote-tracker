using Npgsql;

namespace Meridian.Tests.Persistence;

/// <summary>
/// Checks that the database itself rejects data that breaks the rules in docs/DATABASE.md.
/// Each test runs in its own transaction that is rolled back, so tests do not see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchemaConstraintTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;
    private const string ExclusionViolation = PostgresErrorCodes.ExclusionViolation;

    private NpgsqlConnection _connection = null!;
    private NpgsqlTransaction _transaction = null!;

    public async Task InitializeAsync()
    {
        _connection = await fixture.OpenAsync();
        _transaction = await _connection.BeginTransactionAsync();

        // Minimal approved data: one country, one import with three rows, one vote with a FRA ballot.
        await Exec("""
            INSERT INTO countries (country_code, name) VALUES ('FRA', 'France'), ('DEU', 'Germany');
            INSERT INTO imports (id, file_name, storage_uri, sha256, uploaded_by_id, new_count, unchanged_count,
                                 duplicate_count, invalid_count, contradiction_count, conflict_count, normalized_count)
                OVERRIDING SYSTEM VALUE
                VALUES (1, 'q1.csv', 'local://q1.csv', repeat('a', 64), 1, 1, 0, 0, 0, 0, 2, 0);
            INSERT INTO import_rows (import_id, row_number, raw_line, normalized, classification)
                VALUES (1, 1, 'r1', false, 'New'), (1, 2, 'r2', false, 'Conflict'), (1, 3, 'r3', false, 'Conflict');
            INSERT INTO votes (id, assembly_vote_id, session, vote_date, title, first_import_id, source_import_id)
                OVERRIDING SYSTEM VALUE
                VALUES (1, 'MA/2024/001', 79, '2024-01-12', 'Desertification', 1, 1);
            INSERT INTO country_votes (vote_id, country_code, ballot, source_import_id, source_row_number, approved_by_id, approved_at)
                VALUES (1, 'FRA', 'Y', 1, 1, 1, now());
            """);
    }

    public async Task DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("y")]
    [InlineData("")]
    public async Task Ballot_outside_YNAX_is_rejected(string ballot)
    {
        var ex = await Fails($"""
            INSERT INTO country_votes (vote_id, country_code, ballot, source_import_id, source_row_number, approved_by_id, approved_at)
            VALUES (1, 'DEU', '{ballot}', 1, 1, 1, now());
            """);

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_country_votes_ballot", ex.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Weight_outside_1_to_5_is_rejected(int weight)
    {
        var ex = await Fails($"INSERT INTO vote_positions (vote_id, desired_outcome, weight, set_by_id, set_at) VALUES (1, 'Y', {weight}, 1, now());");

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_vote_positions_weight", ex.ConstraintName);
    }

    [Fact]
    public async Task Weight_1_and_5_are_accepted()
    {
        await Exec("INSERT INTO vote_positions (vote_id, desired_outcome, weight, set_by_id, set_at) VALUES (1, 'N', 5, 1, now());");
        await Exec("UPDATE vote_positions SET weight = 1 WHERE vote_id = 1;");
    }

    [Fact]
    public async Task Desired_outcome_other_than_Y_or_N_is_rejected()
    {
        var ex = await Fails("INSERT INTO vote_positions (vote_id, desired_outcome, weight, set_by_id, set_at) VALUES (1, 'A', 3, 1, now());");

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_vote_positions_desired_outcome", ex.ConstraintName);
    }

    [Fact]
    public async Task Duplicate_assembly_vote_id_is_rejected()
    {
        var ex = await Fails("""
            INSERT INTO votes (id, assembly_vote_id, session, vote_date, title, first_import_id, source_import_id) OVERRIDING SYSTEM VALUE
            VALUES (2, 'MA/2024/001', 79, '2024-01-12', 'Again', 1, 1);
            """);

        Assert.Equal(UniqueViolation, ex.SqlState);
        Assert.Equal("ux_votes_assembly_vote_id", ex.ConstraintName);
    }

    [Fact]
    public async Task Second_ballot_for_same_country_and_vote_is_rejected()
    {
        var ex = await Fails("""
            INSERT INTO country_votes (vote_id, country_code, ballot, source_import_id, source_row_number, approved_by_id, approved_at)
            VALUES (1, 'FRA', 'N', 1, 2, 1, now());
            """);

        Assert.Equal(UniqueViolation, ex.SqlState);
        Assert.Equal("pk_country_votes", ex.ConstraintName);
    }

    [Fact]
    public async Task Duplicate_pending_country_vote_conflict_is_rejected()
    {
        await InsertCountryVoteConflict("A", "Pending", row: 2);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertCountryVoteConflict("A", "Pending", row: 3));

        Assert.Equal(UniqueViolation, ex.SqlState);
        Assert.Equal("ux_conflicts_pending_target", ex.ConstraintName);
    }

    [Fact]
    public async Task Duplicate_pending_vote_field_conflict_is_rejected_although_country_code_is_null()
    {
        await InsertVoteFieldConflict("title", "Desertification and Drought", row: 2);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertVoteFieldConflict("title", "Desertification and Drought", row: 3));

        Assert.Equal(UniqueViolation, ex.SqlState);
        Assert.Equal("ux_conflicts_pending_target", ex.ConstraintName);
    }

    [Fact]
    public async Task Same_proposed_value_for_a_different_field_is_a_separate_conflict()
    {
        await InsertVoteFieldConflict("session", "80", row: 2);
        await InsertVoteFieldConflict("title", "80", row: 3);
    }

    [Fact]
    public async Task Decided_conflicts_do_not_block_a_later_change_Y_A_Y_A()
    {
        // Approved FRA = Y. Each file proposes a change, an analyst approves it, then the next file changes it back.
        var first = await InsertCountryVoteConflict("A", "Pending", row: 2);
        await Decide(first, "Approved");

        var second = await InsertCountryVoteConflict("Y", "Pending", row: 3);
        await Decide(second, "Approved");

        var third = await InsertCountryVoteConflict("A", "Pending", row: 2);

        Assert.Equal(3L, await Scalar<long>("SELECT count(*) FROM conflicts WHERE vote_id = 1 AND country_code = 'FRA';"));
        Assert.Equal("Pending", await Scalar<string>($"SELECT status FROM conflicts WHERE id = {third};"));

        // While the third is open, the same proposal still cannot be opened twice.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertCountryVoteConflict("A", "Pending", row: 3));
        Assert.Equal("ux_conflicts_pending_target", ex.ConstraintName);
    }

    [Fact]
    public async Task Vote_field_conflict_on_unknown_field_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertVoteFieldConflict("resolution_title", "x", row: 2));

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_conflicts_target", ex.ConstraintName);
    }

    [Fact]
    public async Task Decided_conflict_without_decider_is_rejected()
    {
        var ex = await Fails("""
            INSERT INTO conflicts (vote_id, kind, country_code, approved_value, proposed_value,
                                   approved_source_import_id, proposed_source_import_id, proposed_source_row_number, status)
            VALUES (1, 'CountryVote', 'FRA', 'Y', 'A', 1, 1, 2, 'Approved');
            """);

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_conflicts_decision", ex.ConstraintName);
    }

    [Theory]
    [InlineData("2010-01-01", "2012-12-31")]  // inside the closed period
    [InlineData("2019-12-31", null)]          // open-ended, starts on the last day of the closed period
    [InlineData("1990-01-01", "1995-01-01")]  // ends on the first day of the closed period
    public async Task Overlapping_membership_periods_are_rejected(string from, string? to)
    {
        await Exec("INSERT INTO country_memberships (country_code, member_from, member_to) VALUES ('FRA', '1995-01-01', '2019-12-31');");

        var ex = await Fails($"INSERT INTO country_memberships (country_code, member_from, member_to) VALUES ('FRA', '{from}', {Literal(to)});");

        Assert.Equal(ExclusionViolation, ex.SqlState);
        Assert.Equal("ex_country_memberships_no_overlap", ex.ConstraintName);
    }

    [Fact]
    public async Task Open_ended_period_blocks_any_later_period()
    {
        await Exec("INSERT INTO country_memberships (country_code, member_from, member_to) VALUES ('FRA', '1995-01-01', NULL);");

        var ex = await Fails("INSERT INTO country_memberships (country_code, member_from, member_to) VALUES ('FRA', '2030-01-01', '2031-01-01');");

        Assert.Equal(ExclusionViolation, ex.SqlState);
    }

    [Fact]
    public async Task Adjacent_periods_and_other_countries_are_accepted()
    {
        await Exec("""
            INSERT INTO country_memberships (country_code, member_from, member_to) VALUES
                ('FRA', '1995-01-01', '2019-12-31'),
                ('FRA', '2020-01-01', NULL),
                ('DEU', '1995-01-01', NULL);
            """);
    }

    [Fact]
    public async Task Membership_ending_before_it_starts_is_rejected()
    {
        var ex = await Fails("INSERT INTO country_memberships (country_code, member_from, member_to) VALUES ('FRA', '2020-01-01', '2019-01-01');");

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_country_memberships_period", ex.ConstraintName);
    }

    [Fact]
    public async Task Second_published_snapshot_in_a_quarter_is_rejected()
    {
        await InsertSnapshot("Published", "'2024-Q1'");
        await InsertSnapshot("Saved", "'2024-Q1'");
        await InsertSnapshot("Published", "'2024-Q2'");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertSnapshot("Published", "'2024-Q1'"));

        Assert.Equal(UniqueViolation, ex.SqlState);
        Assert.Equal("ux_snapshots_published_quarter", ex.ConstraintName);
    }

    [Fact]
    public async Task Unpublishing_back_to_saved_frees_the_quarter()
    {
        var first = await InsertSnapshot("Published", "'2024-Q1'");
        await Exec($"UPDATE snapshots SET status = 'Saved' WHERE id = {first};");

        await InsertSnapshot("Published", "'2024-Q1'");
    }

    [Fact]
    public async Task Published_snapshot_without_quarter_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertSnapshot("Published", "NULL"));

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_snapshots_published", ex.ConstraintName);
    }

    [Fact]
    public async Task Calculating_snapshot_may_have_no_quarter()
    {
        await InsertSnapshot("Calculating", "NULL");
    }

    [Fact]
    public async Task All_tables_and_columns_are_snake_case()
    {
        var offenders = new List<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
              AND (table_name !~ '^[a-z][a-z0-9_]*$' OR column_name !~ '^[a-z][a-z0-9_]*$');
            """, _connection, _transaction))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                offenders.Add(reader.GetString(0));

        Assert.Empty(offenders);
    }

    private Task<long> InsertCountryVoteConflict(string proposed, string status, int row) => Scalar<long>($"""
        INSERT INTO conflicts (vote_id, kind, country_code, approved_value, proposed_value,
                               approved_source_import_id, proposed_source_import_id, proposed_source_row_number, status)
        VALUES (1, 'CountryVote', 'FRA', 'Y', '{proposed}', 1, 1, {row}, '{status}')
        RETURNING id;
        """);

    private Task<long> InsertVoteFieldConflict(string field, string proposed, int row) => Scalar<long>($"""
        INSERT INTO conflicts (vote_id, kind, field_name, approved_value, proposed_value,
                               approved_source_import_id, proposed_source_import_id, proposed_source_row_number, status)
        VALUES (1, 'VoteField', '{field}', 'old', '{proposed}', 1, 1, {row}, 'Pending')
        RETURNING id;
        """);

    private Task Decide(long conflictId, string status) =>
        Exec($"UPDATE conflicts SET status = '{status}', decided_by_id = 1, decided_at = now() WHERE id = {conflictId} AND status = 'Pending';");

    private Task<long> InsertSnapshot(string status, string quarterSql)
    {
        var published = status == "Published" ? "1, now()" : "NULL, NULL";
        return Scalar<long>($"""
            INSERT INTO snapshots (name, filter, rules, status, quarter, created_by_id, published_by_id, published_at)
            VALUES ('s', '{"{}"}', '{"{}"}', '{status}', {quarterSql}, 1, {published})
            RETURNING id;
            """);
    }

    private static string Literal(string? value) => value is null ? "NULL" : $"'{value}'";

    private async Task Exec(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, _connection, _transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, _connection, _transaction);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<PostgresException> Fails(string sql) => Assert.ThrowsAsync<PostgresException>(() => Exec(sql));
}
