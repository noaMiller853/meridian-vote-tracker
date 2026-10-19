using Meridian.Domain.Ingestion;
using static Meridian.Tests.Domain.Ingest;

namespace Meridian.Tests.Domain;

/// <summary>ING-05/ING-06 classification of the rows of one vote, on the spec example (7.2) and real q1/rev2 cases.</summary>
public class VoteClassifierTests
{
    private static RowClassification[] Classes(VoteClassification v) => v.Rows.Select(r => r.Classification).ToArray();

    // ---- Spec 7.2, first upload (original file) ----

    [Fact]
    public void Spec_example_first_upload()
    {
        // Votes 101..104 on 2024-01-11. Rows 1..8 exactly as in the example table.
        const string d = ",79,2024-01-11,";
        var v101 = Classify("101", null, (1, "101" + d + "T101,BRA,Brazil,Y,"), (2, "101" + d + "T101,IND,India,Y,"));
        var v102 = Classify("102", null, (3, "102" + d + "T102,JPN,Japan,Y,"), (4, "102" + d + "T102,JPN,Japan,Y,"));
        var v103 = Classify("103", null, (5, "103" + d + "T103,FRA,France,Y,"), (6, "103" + d + "T103,FRA,France,N,"));
        var v104 = Classify("104", null, (7, "104" + d + "T104,FRN,France,X,"), (8, "104" + d + "T104,NZL,New Zealand,Y,"));

        Assert.Equal([RowClassification.New, RowClassification.New], Classes(v101));
        Assert.Equal([RowClassification.New, RowClassification.Duplicate], Classes(v102));
        Assert.Equal("identical to line 3", v102.Rows[1].Reason);
        Assert.Equal([RowClassification.Contradiction, RowClassification.Contradiction], Classes(v103));
        Assert.Equal("FRA appears with different ballots: Y (line 5), N (line 6)", v103.Rows[0].Reason);
        Assert.Equal([RowClassification.Invalid, RowClassification.Invalid], Classes(v104));
        Assert.Equal("country_code 'FRN' is not a member state", v104.Rows[0].Reason);
        Assert.Equal("NZL is not a member on 2024-01-11", v104.Rows[1].Reason);

        Assert.Equal(VoteStatus.New, v101.Status);
        Assert.Equal(VoteStatus.NoValidRows, v104.Status);
    }

    // ---- Spec 7.2, second upload (corrected file), against the data approved by the first upload ----

    [Fact]
    public void Spec_example_second_upload()
    {
        const string d = ",79,2024-01-11,";
        var approved101 = Existing(79, "2024-01-11", "T101", new Dictionary<string, Ballot> { ["BRA"] = Ballot.Y, ["IND"] = Ballot.Y });

        // The title of 101 changed in the corrected file.
        var v101 = Classify("101", approved101, (1, "101" + d + "T101 corrected,BRA,Brazil,A,"), (2, "101" + d + "T101 corrected,IND,India,Y,"));
        // 103 and 104 have no approved country votes after the first upload.
        var v103 = Classify("103", null, (3, "103" + d + "T103,FRA,France,N,"));
        var v104 = Classify("104", null, (4, "104" + d + "T104,FRA,France,X,"));
        var v105 = Classify("105", null, (5, "105" + d + "T105,BRA,Brazil,Y,"));

        Assert.Equal([RowClassification.Conflict, RowClassification.Unchanged], Classes(v101));
        Assert.Equal(ConflictAction.Open, v101.Rows[0].Action);
        Assert.Equal("approved Y, file says A", v101.Rows[0].Reason);
        var title = Assert.Single(v101.FieldConflicts);
        Assert.Equal(new VoteFieldChange("title", "T101", "T101 corrected", ConflictAction.Open), title);

        Assert.Equal(RowClassification.New, Assert.Single(v103.Rows).Classification);
        Assert.Equal(RowClassification.New, Assert.Single(v104.Rows).Classification);
        Assert.Equal(RowClassification.New, Assert.Single(v105.Rows).Classification);
        // Vote 102 is not in the file: nothing is classified for it, so nothing can change it (ING-09).
    }

    [Fact]
    public void Re_uploading_the_corrected_file_opens_nothing_new()
    {
        const string d = ",79,2024-01-11,";
        var approved101 = Existing(79, "2024-01-11", "T101", new Dictionary<string, Ballot> { ["BRA"] = Ballot.Y, ["IND"] = Ballot.Y },
            pending: [ConflictKey.ForCountry("BRA", Ballot.A), ConflictKey.ForField("title", "T101 corrected")]);

        var v101 = Classify("101", approved101, (1, "101" + d + "T101 corrected,BRA,Brazil,A,"), (2, "101" + d + "T101 corrected,IND,India,Y,"));

        // Same classes as the first time (DECISIONS §5), but no second conflict.
        Assert.Equal([RowClassification.Conflict, RowClassification.Unchanged], Classes(v101));
        Assert.Equal(ConflictAction.AlreadyPending, v101.Rows[0].Action);
        Assert.Equal(ConflictAction.AlreadyPending, Assert.Single(v101.FieldConflicts).Action);
    }

    // ---- Real q1 cases ----

    [Fact]
    public void Real_q1_MA_2024_007_exact_copies_are_duplicates()
    {
        const string p = "MA/2024/007,79,2024-01-22,Nuclear Non-Proliferation and Regional Cooperation,";
        var v = Classify("MA/2024/007", null,
            (896, p + "AFG,Afghanistan,Y,2024-04-02T09:00:00Z"),
            (897, p + "ALB,Albania,A,2024-04-02T09:00:00Z"),
            (1045, p + "AFG,Afghanistan,Y,2024-04-02T09:00:00Z"),
            (1046, p + "ALB,Albania,A,2024-04-02T09:00:00Z"));

        Assert.Equal([RowClassification.New, RowClassification.New, RowClassification.Duplicate, RowClassification.Duplicate], Classes(v));
        Assert.Equal("identical to line 896", v.Rows[2].Reason);
        Assert.Equal("identical to line 897", v.Rows[3].Reason);
    }

    [Fact]
    public void Real_q1_MA_2024_012_FRA_with_two_ballots_is_a_contradiction_and_other_countries_are_not_affected()
    {
        const string p = "MA/2024/012,79,2024-02-02,\"Small Island Developing States, Navigation and Trade\",";
        var v = Classify("MA/2024/012", null,
            (1694, p + "FIN,Finland,N,2024-04-02T09:00:00Z"),
            (1695, p + "FRA,France,Y,2024-04-02T09:00:00Z"),
            (1793, p + "FRA,France,N,2024-04-02T09:00:00Z"));

        Assert.Equal([RowClassification.New, RowClassification.Contradiction, RowClassification.Contradiction], Classes(v));
        Assert.Equal("FRA appears with different ballots: Y (line 1695), N (line 1793)", v.Rows[1].Reason);
        Assert.Equal(VoteStatus.New, v.Status);
    }

    private const string Q1Vote025 = "MA/2024/025,79,2024-02-18,";

    [Fact]
    public void Real_q1_MA_2024_025_double_space_alone_is_normalized_and_the_vote_is_ingested()
    {
        var v = Classify("MA/2024/025", null,
            (3582, Q1Vote025 + "Nuclear Non-Proliferation and the Role of Regional Organizations,AFG,Afghanistan,N,2024-04-02T09:00:00Z"),
            (3592, Q1Vote025 + "Nuclear  Non-Proliferation and the Role of Regional Organizations,BHS,Bahamas,N,2024-04-02T09:00:00Z"));

        Assert.Equal(VoteStatus.New, v.Status);
        Assert.Equal([RowClassification.New, RowClassification.New], Classes(v));
        Assert.Equal("Nuclear Non-Proliferation and the Role of Regional Organizations", v.Details!.Title);
    }

    [Fact]
    public void Real_q1_MA_2024_025_draft_title_makes_every_valid_row_of_the_vote_a_contradiction()
    {
        var v = Classify("MA/2024/025", null,
            (3582, Q1Vote025 + "Nuclear Non-Proliferation and the Role of Regional Organizations,AFG,Afghanistan,N,2024-04-02T09:00:00Z"),
            (3583, Q1Vote025 + "Nuclear Non-Proliferation and the Role of Regional Organizations,ALB,Albania,Y,2024-04-02T09:00:00Z"),
            (3592, Q1Vote025 + "Nuclear  Non-Proliferation and the Role of Regional Organizations,BHS,Bahamas,N,2024-04-02T09:00:00Z"),
            (3593, Q1Vote025 + "Nuclear Non-Proliferation and the Role of Regional Organizations (draft),BHR,Bahrain,N,2024-04-02T09:00:00Z"),
            (3594, Q1Vote025 + "Nuclear Non-Proliferation and the Role of Regional Organizations,BGD,Bangladesh,Y,2024-04-02T09:00:00Z"));

        Assert.Equal(VoteStatus.Contradiction, v.Status);
        Assert.Null(v.Details);
        Assert.All(v.Rows, r => Assert.Equal(RowClassification.Contradiction, r.Classification));
        // The double-space row joined the main title after normalization; only "(draft)" is a separate variant.
        Assert.Equal(
            "vote details differ between rows: " +
            "resolution_title 'Nuclear Non-Proliferation and the Role of Regional Organizations' on 4 rows (lines 3582, 3583, 3592, 3594); " +
            "resolution_title 'Nuclear Non-Proliferation and the Role of Regional Organizations (draft)' on 1 row (line 3593)",
            v.ContradictionReason);
        Assert.Equal(v.ContradictionReason, v.Rows[0].Reason);
    }

    [Fact]
    public void Invalid_rows_take_no_part_in_the_consistency_check()
    {
        // Real: q1 MA/2024/027, 6 rows with 21/02/2024 next to ISO rows. The bad date must not create a contradiction.
        const string t = "Landmine Clearance and Regional Cooperation";
        var v = Classify("MA/2024/027", null,
            (3700, $"MA/2024/027,79,2024-02-21,{t},AFG,Afghanistan,X,2024-04-02T09:00:00Z"),
            (3701, $"MA/2024/027,79,21/02/2024,{t},ALB,Albania,Y,2024-04-02T09:00:00Z"));

        Assert.Equal([RowClassification.New, RowClassification.Invalid], Classes(v));
        Assert.Equal(VoteStatus.New, v.Status);
    }

    [Fact]
    public void Contradiction_on_session_or_date_lists_only_the_fields_that_differ()
    {
        var v = Classify("MA/2024/050", null,
            (10, "MA/2024/050,79,2024-02-05,Title,AFG,Afghanistan,Y,"),
            (11, "MA/2024/050,80,2024-02-06,Title,ALB,Albania,Y,"));

        Assert.Equal(
            "vote details differ between rows: session 79, vote_date 2024-02-05 on 1 row (line 10); session 80, vote_date 2024-02-06 on 1 row (line 11)",
            v.ContradictionReason);
    }

    [Fact]
    public void Long_line_lists_are_shortened()
    {
        var lines = Enumerable.Range(100, 12).Select(i => (i, $"MA/2024/060,79,2024-02-05,Title,AFG,Afghanistan,Y,")).ToList();
        lines.Add((200, "MA/2024/060,79,2024-02-05,Other,ALB,Albania,Y,"));

        var v = Classify("MA/2024/060", null, lines.ToArray());

        Assert.Contains("on 12 rows (lines 100, 101, 102, 103, 104, 105, 106, 107, 108, 109 and 2 more)", v.ContradictionReason);
    }

    // ---- Real rev2 cases against q1 approved data ----

    [Fact]
    public void Real_rev2_MA_2024_009_title_change_is_a_vote_field_conflict_and_ballots_stay_unchanged()
    {
        var approved = Existing(79, "2024-01-27", "Biological Diversity in Coastal Zones", new Dictionary<string, Ballot> { ["LUX"] = Ballot.Y });

        var v = Classify("MA/2024/009", approved,
            (200, "MA/2024/009,79,2024-01-27,Biological Diversity in Coastal Zones: Revised,LUX,Luxembourg,Y,2024-04-20T09:00:00Z"));

        Assert.Equal(RowClassification.Unchanged, Assert.Single(v.Rows).Classification);
        Assert.Equal(
            new VoteFieldChange("title", "Biological Diversity in Coastal Zones", "Biological Diversity in Coastal Zones: Revised", ConflictAction.Open),
            Assert.Single(v.FieldConflicts));
    }

    [Fact]
    public void Real_rev2_MA_2024_014_date_change_is_a_vote_field_conflict()
    {
        var approved = Existing(79, "2024-02-05", "Polar Research in Coastal Zones", new Dictionary<string, Ballot> { ["NER"] = Ballot.N });

        var v = Classify("MA/2024/014", approved,
            (300, "MA/2024/014,79,2024-02-06,Polar Research in Coastal Zones,NER,Niger,N,2024-04-20T09:00:00Z"));

        Assert.Equal(new VoteFieldChange("vote_date", "2024-02-05", "2024-02-06", ConflictAction.Open), Assert.Single(v.FieldConflicts));
    }

    [Fact]
    public void Real_rev2_MA_2024_003_changed_ballots_are_conflicts()
    {
        var approved = Existing(79, "2024-01-16", "Food Security in Coastal Zones",
            new Dictionary<string, Ballot> { ["BRA"] = Ballot.Y, ["IND"] = Ballot.Y, ["CAN"] = Ballot.N });

        var v = Classify("MA/2024/003", approved,
            (400, "MA/2024/003,79,2024-01-16,Food Security in Coastal Zones,CAN,Canada,N,2024-04-20T09:00:00Z"),
            (401, "MA/2024/003,79,2024-01-16,Food Security in Coastal Zones,BRA,Brazil,A,2024-04-20T09:00:00Z"),
            (402, "MA/2024/003,79,2024-01-16,Food Security in Coastal Zones,IND,India,N,2024-04-20T09:00:00Z"));

        Assert.Equal([RowClassification.Unchanged, RowClassification.Conflict, RowClassification.Conflict], Classes(v));
        Assert.Equal("approved Y, file says N", v.Rows[2].Reason);
        Assert.Empty(v.FieldConflicts);
    }

    [Fact]
    public void Country_missing_from_an_approved_vote_is_new_not_a_conflict()
    {
        // Real: MA/2024/031 has 80 rows in q1; rev2 adds the rest (ING-10).
        var approved = Existing(79, "2024-02-25", "Counter-Terrorism Cooperation and the Role of Regional Organizations",
            new Dictionary<string, Ballot> { ["AFG"] = Ballot.Y });

        var v = Classify("MA/2024/031", approved,
            (500, "MA/2024/031,79,2024-02-25,Counter-Terrorism Cooperation and the Role of Regional Organizations,AFG,Afghanistan,Y,"),
            (501, "MA/2024/031,79,2024-02-25,Counter-Terrorism Cooperation and the Role of Regional Organizations,ZMB,Zambia,N,"));

        Assert.Equal([RowClassification.Unchanged, RowClassification.New], Classes(v));
        Assert.Equal("country missing from the approved vote is added (ING-10)", v.Rows[1].Reason);
    }

    // ---- Conflict actions ----

    [Fact]
    public void Proposal_rejected_from_the_same_file_stays_a_conflict_but_opens_nothing()
    {
        var approved = Existing(79, "2024-01-11", "T", new Dictionary<string, Ballot> { ["BRA"] = Ballot.Y },
            rejected: [ConflictKey.ForCountry("BRA", Ballot.A)]);

        var v = Classify("101", approved, (1, "101,79,2024-01-11,T,BRA,Brazil,A,"));

        Assert.Equal((RowClassification.Conflict, ConflictAction.AlreadyRejected), (v.Rows[0].Classification, v.Rows[0].Action));
    }

    [Fact]
    public void Pending_conflict_for_a_different_value_does_not_block_a_new_one()
    {
        var approved = Existing(79, "2024-01-11", "T", new Dictionary<string, Ballot> { ["BRA"] = Ballot.Y },
            pending: [ConflictKey.ForCountry("BRA", Ballot.N)]);

        var v = Classify("101", approved, (1, "101,79,2024-01-11,T,BRA,Brazil,A,"));

        Assert.Equal(ConflictAction.Open, v.Rows[0].Action);
    }

    [Fact]
    public void Y_A_Y_A_each_change_opens_a_conflict_once_the_previous_one_is_decided()
    {
        ConflictAction ActionWhen(Ballot approved, Ballot proposed) =>
            Classify("101", Existing(79, "2024-01-11", "T", new Dictionary<string, Ballot> { ["BRA"] = approved }),
                (1, $"101,79,2024-01-11,T,BRA,Brazil,{proposed},")).Rows[0].Action!.Value;

        Assert.Equal(ConflictAction.Open, ActionWhen(Ballot.Y, Ballot.A));
        Assert.Equal(ConflictAction.Open, ActionWhen(Ballot.A, Ballot.Y));
        Assert.Equal(ConflictAction.Open, ActionWhen(Ballot.Y, Ballot.A));
    }

    // ---- General ----

    [Fact]
    public void Copies_of_one_side_of_a_contradiction_are_contradictions_too()
    {
        var v = Classify("103", null,
            (5, "103,79,2024-01-11,T,FRA,France,Y,"),
            (6, "103,79,2024-01-11,T,FRA,France,Y,"),
            (7, "103,79,2024-01-11,T,FRA,France,N,"));

        Assert.All(v.Rows, r => Assert.Equal(RowClassification.Contradiction, r.Classification));
    }

    [Fact]
    public void Rows_that_differ_only_in_name_or_case_are_duplicates()
    {
        var v = Classify("101", null,
            (1, "101,79,2024-01-11,T,TUR,Turkey,Y,"),
            (2, "101,79,2024-01-11,T,tur,Türkiye,y,"));

        Assert.Equal([RowClassification.New, RowClassification.Duplicate], Classes(v));
    }

    [Fact]
    public void Every_row_gets_exactly_one_classification_in_line_order()
    {
        var v = Classify("MA/2024/070", null,
            (9, "MA/2024/070,79,2024-02-05,T,FRA,France,N,"),
            (3, "MA/2024/070,79,2024-02-05,T,FRN,France,Y,"),
            (5, "MA/2024/070,79,2024-02-05,T,FRA,France,Y,"),
            (4, "MA/2024/070,79,2024-02-05,T,DEU,Germany,Y,"),
            (7, "MA/2024/070,79,2024-02-05,T,DEU,Germany,Y,"));

        Assert.Equal([3, 4, 5, 7, 9], v.Rows.Select(r => r.LineNumber));
        Assert.Equal(
            [RowClassification.Invalid, RowClassification.New, RowClassification.Contradiction, RowClassification.Duplicate, RowClassification.Contradiction],
            Classes(v));
    }
}
