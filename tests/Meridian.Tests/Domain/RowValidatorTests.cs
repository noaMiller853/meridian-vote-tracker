using Meridian.Domain.Ingestion;
using static Meridian.Tests.Domain.Ingest;

namespace Meridian.Tests.Domain;

/// <summary>ING-04 checks and ING-13 normalizations, mostly on real lines from q1 and the historic file.</summary>
public class RowValidatorTests
{
    private const string Q1Line2 = "MA/2024/001,79,2024-01-12,\"Desertification, Including Capacity Building\",AFG,Afghanistan,N,2024-04-02T09:00:00Z";

    [Fact]
    public void Clean_real_row_is_valid_without_normalizations()
    {
        var row = Assert.IsType<ValidRow>(Validate(2, Q1Line2));

        Assert.Equal("MA/2024/001", row.AssemblyVoteId);
        Assert.Equal(new VoteDetails(79, new DateOnly(2024, 1, 12), "Desertification, Including Capacity Building"), row.Details);
        Assert.Equal(("AFG", Ballot.N, "Afghanistan"), (row.CountryCode, row.Ballot, row.CountryName));
        Assert.Empty(row.Normalizations);
        Assert.Empty(row.EmptyOptionalFields);
    }

    // ---- Normalizations (ING-13): applied, accepted and reported with the original value ----

    [Fact]
    public void Lower_case_country_code_is_normalized_and_reported()
    {
        // Real: q1 line 2594.
        var row = Assert.IsType<ValidRow>(Validate(2594,
            "MA/2024/018,79,2024-02-10,\"Indigenous Languages, Including Capacity Building\",deu,Germany,X,2024-04-02T09:00:00Z"));

        Assert.Equal("DEU", row.CountryCode);
        Assert.Equal([new Normalization("country_code", "deu", "DEU")], row.Normalizations);
    }

    [Fact]
    public void Padded_lower_case_country_code_is_normalized_and_reported()
    {
        // Illustrative example from DECISIONS §1; no real file contains it.
        var row = Assert.IsType<ValidRow>(Validate(5, "MA/2024/001,79,2024-01-12,Desertification, fra ,France,Y,2024-04-02T09:00:00Z"));

        Assert.Equal("FRA", row.CountryCode);
        Assert.Equal([new Normalization("country_code", " fra ", "FRA")], row.Normalizations);
    }

    [Theory]
    [InlineData("y", Ballot.Y)]
    [InlineData(" n ", Ballot.N)]
    [InlineData("a", Ballot.A)]
    public void Ballot_case_and_padding_are_normalized_and_reported(string ballot, Ballot expected)
    {
        var row = Assert.IsType<ValidRow>(Validate(5, $"MA/2024/001,79,2024-01-12,Desertification,FRA,France,{ballot},2024-04-02T09:00:00Z"));

        Assert.Equal(expected, row.Ballot);
        Assert.Equal([new Normalization("ballot", ballot, expected.ToString())], row.Normalizations);
    }

    [Fact]
    public void Double_space_in_title_is_collapsed_and_reported()
    {
        // Real: q1 line 3592, MA/2024/025.
        var row = Assert.IsType<ValidRow>(Validate(3592,
            "MA/2024/025,79,2024-02-18,Nuclear  Non-Proliferation and the Role of Regional Organizations,BHS,Bahamas,N,2024-04-02T09:00:00Z"));

        Assert.Equal("Nuclear Non-Proliferation and the Role of Regional Organizations", row.Details.Title);
        Assert.Equal(
            [new Normalization("resolution_title",
                "Nuclear  Non-Proliferation and the Role of Regional Organizations",
                "Nuclear Non-Proliferation and the Role of Regional Organizations")],
            row.Normalizations);
    }

    [Fact]
    public void Title_is_trimmed_but_case_and_text_are_never_changed()
    {
        var padded = Assert.IsType<ValidRow>(Validate(5, "MA/2024/001,79,2024-01-12,  Food   Security\tin Coastal Zones ,FRA,France,Y,"));
        Assert.Equal("Food Security in Coastal Zones", padded.Details.Title);

        // Real: q1 line 3593. "(draft)" and the case stay exactly as published.
        var draft = Assert.IsType<ValidRow>(Validate(3593,
            "MA/2024/025,79,2024-02-18,Nuclear Non-Proliferation and the Role of Regional Organizations (draft),BHR,Bahrain,N,2024-04-02T09:00:00Z"));
        Assert.Equal("Nuclear Non-Proliferation and the Role of Regional Organizations (draft)", draft.Details.Title);
        Assert.Empty(draft.Normalizations);

        var lower = Assert.IsType<ValidRow>(Validate(5, "MA/2024/001,79,2024-01-12,food security,FRA,France,Y,"));
        Assert.Equal("food security", lower.Details.Title);
    }

    [Fact]
    public void Several_normalizations_on_one_row_are_all_reported()
    {
        var row = Assert.IsType<ValidRow>(Validate(5, "MA/2024/001,79,2024-01-12,Food  Security, fra ,France,y,"));

        Assert.Equal(["resolution_title", "country_code", "ballot"], row.Normalizations.Select(n => n.Field));
    }

    // ---- Ballot ----

    [Theory]
    [InlineData(2842, "MA/2024/020,79,2024-02-14,Counter-Terrorism,ARG,Argentina,Yes,2024-04-02T09:00:00Z", "Yes")] // q1
    [InlineData(319853, "MA/2015/562,70,2015-11-05,Rights of Persons with Disabilities,NPL,Nepal,YES,2015-11-07T09:00:00Z", "YES")] // historic
    [InlineData(340903, "MA/2020/203,75,2020-04-29,Peacekeeping Operations,CPV,Cabo Verde,?,2020-05-01T09:00:00Z", "?")] // historic
    [InlineData(235011, "MA/2019/095,74,2019-01-30,Arms Trade Transparency,ALB,Albania,0,2019-02-01T09:00:00Z", "0")] // historic
    public void Ballot_outside_YNAX_is_invalid_with_the_original_value(int line, string csv, string original)
    {
        var row = Assert.IsType<InvalidRow>(Validate(line, csv));

        var problem = Assert.Single(row.Problems);
        Assert.Equal(RowProblemCode.InvalidBallot, problem.Code);
        Assert.Equal(original, problem.OriginalValue);
        Assert.Equal($"ballot '{original}' is not one of Y, N, A, X", problem.Message);
        Assert.Empty(row.Normalizations); // "Yes" -> "YES" is not reported as a normalization
    }

    [Fact]
    public void Empty_ballot_is_a_missing_field()
    {
        // Real: q1 line 2846.
        var row = Assert.IsType<InvalidRow>(Validate(2846,
            "MA/2024/020,79,2024-02-14,\"Counter-Terrorism Cooperation, Navigation and Trade\",AZE,Azerbaijan,,2024-04-02T09:00:00Z"));

        var problem = Assert.Single(row.Problems);
        Assert.Equal((RowProblemCode.MissingField, "ballot", "ballot is empty"), (problem.Code, problem.Field, problem.Message));
    }

    // ---- Date ----

    [Theory]
    [InlineData("21/02/2024")] // real: q1, 6 rows of MA/2024/027
    [InlineData("21-02-2024")]
    [InlineData("2024-02-30")] // not a real date
    [InlineData("2024-2-21")]
    [InlineData(" 2024-02-21")]
    [InlineData("21 Feb 2024")]
    public void Date_must_be_a_real_yyyy_mm_dd_date(string date)
    {
        var row = Assert.IsType<InvalidRow>(Validate(3600, $"MA/2024/027,79,{date},Landmine Clearance and Regional Cooperation,AFG,Afghanistan,X,2024-04-02T09:00:00Z"));

        var problem = Assert.Single(row.Problems);
        Assert.Equal(RowProblemCode.InvalidDate, problem.Code);
        Assert.Equal(date, problem.OriginalValue);
        Assert.Equal($"vote_date '{date}' is not a valid yyyy-mm-dd date", problem.Message);
    }

    [Fact]
    public void Leap_day_is_a_real_date()
    {
        Assert.IsType<ValidRow>(Validate(5, "MA/2024/034,79,2024-02-29,Maritime Safety in Coastal Zones,FRA,France,Y,"));
    }

    // ---- Country and membership ----

    [Fact]
    public void Unknown_country_code_is_invalid()
    {
        // Real: q1 line 2143.
        var row = Assert.IsType<InvalidRow>(Validate(2143,
            "MA/2024/015,79,2024-02-05,\"Climate Adaptation Finance, Including Capacity Building\",FRN,France,X,2024-04-02T09:00:00Z"));

        var problem = Assert.Single(row.Problems);
        Assert.Equal((RowProblemCode.UnknownCountry, "FRN"), (problem.Code, problem.OriginalValue));
        Assert.Equal("country_code 'FRN' is not a member state", problem.Message);
    }

    [Fact]
    public void Country_that_was_not_a_member_on_the_vote_date_is_invalid()
    {
        // Real: q1 line 4854. NZL left on 2021-06-30.
        var row = Assert.IsType<InvalidRow>(Validate(4854,
            "MA/2024/033,79,2024-02-28,\"Migrant Workers, Navigation and Trade\",NZL,New Zealand,Y,2024-04-02T09:00:00Z"));

        var problem = Assert.Single(row.Problems);
        Assert.Equal(RowProblemCode.NotAMember, problem.Code);
        Assert.Equal("NZL is not a member on 2024-02-28", problem.Message);
    }

    [Theory]
    [InlineData("ISL", "2021-03-01", false)] // in Iceland's gap
    [InlineData("ISL", "2022-01-01", true)]  // back on the first day
    [InlineData("TLS", "2022-12-31", false)] // the day before Timor-Leste joined
    [InlineData("TLS", "2023-01-01", true)]
    [InlineData("NZL", "2021-06-30", true)]  // last day of membership
    public void Membership_edges(string code, string date, bool valid)
    {
        var result = Validate(5, $"MA/2021/001,76,{date},Some Resolution,{code},Name,Y,");

        Assert.Equal(valid, result is ValidRow);
    }

    [Fact]
    public void Membership_is_not_checked_when_the_date_is_invalid()
    {
        var row = Assert.IsType<InvalidRow>(Validate(5, "MA/2024/033,79,28/02/2024,Migrant Workers,NZL,New Zealand,Y,"));

        Assert.Equal([RowProblemCode.InvalidDate], row.Problems.Select(p => p.Code));
    }

    // ---- Structure, required fields, session ----

    [Fact]
    public void Wrong_column_count_is_the_only_problem_reported()
    {
        // Real: q1 line 3139 ("AGO;N": a semicolon instead of a comma) and historic line 134746 (no published_at column).
        var q1 = Assert.IsType<InvalidRow>(Validate(3139, "MA/2024/022,79,2024-02-17,Arms Trade Transparency: Follow-up to the Summit,AGO;N"));
        var historic = Assert.IsType<InvalidRow>(Validate(134746, "MA/2020/350,75,2020-07-16,Biological Diversity: Follow-up to the Summit,LVA,,Y"));

        Assert.Equal("expected 8 columns, found 5", Assert.Single(q1.Problems).Message);
        Assert.Equal("expected 8 columns, found 7", Assert.Single(historic.Problems).Message);
        Assert.Equal(RowProblemCode.WrongFieldCount, historic.Problems[0].Code);
    }

    [Theory]
    [InlineData(",79,2024-01-12,Title,FRA,France,Y,", "assembly_vote_id")]
    [InlineData("MA/2024/001,,2024-01-12,Title,FRA,France,Y,", "session")]
    [InlineData("MA/2024/001,79,,Title,FRA,France,Y,", "vote_date")]
    [InlineData("MA/2024/001,79,2024-01-12,   ,FRA,France,Y,", "resolution_title")]
    [InlineData("MA/2024/001,79,2024-01-12,Title,,France,Y,", "country_code")]
    [InlineData("MA/2024/001,79,2024-01-12,Title,FRA,France, ,", "ballot")]
    public void Required_fields_must_not_be_empty(string csv, string field)
    {
        var row = Assert.IsType<InvalidRow>(Validate(5, csv));

        var problem = Assert.Single(row.Problems);
        Assert.Equal((RowProblemCode.MissingField, field, $"{field} is empty"), (problem.Code, problem.Field, problem.Message));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-79")]
    [InlineData("79.0")]
    [InlineData("seventy-nine")]
    public void Session_must_be_a_positive_whole_number(string session)
    {
        var row = Assert.IsType<InvalidRow>(Validate(5, $"MA/2024/001,{session},2024-01-12,Title,FRA,France,Y,"));

        Assert.Equal(RowProblemCode.InvalidSession, Assert.Single(row.Problems).Code);
    }

    [Fact]
    public void Every_problem_of_a_row_is_reported()
    {
        var row = Assert.IsType<InvalidRow>(Validate(5, "MA/2024/001,x,21/02/2024,Title,FRN,France,Yes,"));

        Assert.Equal(
            [RowProblemCode.InvalidSession, RowProblemCode.InvalidDate, RowProblemCode.UnknownCountry, RowProblemCode.InvalidBallot],
            row.Problems.Select(p => p.Code));
        Assert.Equal(
            "session 'x' is not a positive whole number; vote_date '21/02/2024' is not a valid yyyy-mm-dd date; " +
            "country_code 'FRN' is not a member state; ballot 'Yes' is not one of Y, N, A, X",
            row.Reason);
    }

    [Fact]
    public void Normalizations_of_an_invalid_row_are_still_reported()
    {
        var row = Assert.IsType<InvalidRow>(Validate(5, "MA/2024/001,79,21/02/2024,Title,deu,Germany,Y,"));

        Assert.Equal([new Normalization("country_code", "deu", "DEU")], row.Normalizations);
        Assert.Equal("DEU", row.CountryCode);
    }

    // ---- Vote details of an Invalid row (DECISIONS §1: checked separately from the country part) ----

    [Theory]
    [InlineData(2143, "MA/2024/015,79,2024-02-05,Climate Adaptation Finance,FRN,France,X,2024-04-02T09:00:00Z")] // q1: unknown code
    [InlineData(4854, "MA/2024/033,79,2024-02-28,Migrant Workers,NZL,New Zealand,Y,2024-04-02T09:00:00Z")]  // q1: not a member
    [InlineData(2842, "MA/2024/020,79,2024-02-14,Counter-Terrorism,ARG,Argentina,Yes,2024-04-02T09:00:00Z")] // q1: bad ballot
    public void Invalid_country_part_keeps_valid_vote_details(int line, string csv)
    {
        var row = Assert.IsType<InvalidRow>(Validate(line, csv));

        Assert.NotNull(row.Details);
    }

    [Theory]
    [InlineData("MA/2024/027,79,21/02/2024,Landmine Clearance,AFG,Afghanistan,X,")] // bad date
    [InlineData("MA/2024/027,x,2024-02-21,Landmine Clearance,AFG,Afghanistan,X,")]  // bad session
    [InlineData("MA/2024/027,79,2024-02-21,,AFG,Afghanistan,X,")]                   // empty title
    [InlineData(",79,2024-02-21,Landmine Clearance,AFG,Afghanistan,X,")]            // empty vote id
    [InlineData("MA/2024/022,79,2024-02-17,Arms Trade Transparency,AGO;N")]         // q1 line 3139: wrong column count
    public void Invalid_vote_part_gives_no_vote_details(string csv)
    {
        var row = Assert.IsType<InvalidRow>(Validate(5, csv));

        Assert.Null(row.Details);
    }

    // ---- Optional fields ----

    [Fact]
    public void Empty_country_name_and_published_at_are_allowed_and_counted()
    {
        var row = Assert.IsType<ValidRow>(Validate(5, "MA/2024/001,79,2024-01-12,Title,FRA,,Y,"));

        Assert.Equal(["country_name", "published_at"], row.EmptyOptionalFields);
    }
}
