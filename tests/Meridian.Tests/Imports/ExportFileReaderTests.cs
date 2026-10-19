using System.Text;
using Meridian.Domain.Ingestion;
using Meridian.Infrastructure.Imports;

namespace Meridian.Tests.Imports;

/// <summary>The bounded physical-line reader: a damaged line never affects the lines after it.</summary>
public class ExportFileReaderTests
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";
    private const string Good = "MA/2024/001,79,2024-01-12,Desertification,FRA,France,Y,2024-04-02T09:00:00Z";

    private static async Task<List<ExportLine>> Read(string content, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        var lines = new List<ExportLine>();
        await foreach (var line in ExportFileReader.ReadAsync(new MemoryStream(bytes)))
            lines.Add(line);
        return lines;
    }

    private static string File(params string[] dataLines) => string.Join("\n", [Header, .. dataLines]) + "\n";

    [Fact]
    public async Task Good_lines_become_rows_with_their_line_in_the_file()
    {
        var lines = await Read(File(Good, Good.Replace("FRA", "DEU")));

        Assert.Equal([2, 3], lines.Select(l => l.Row.LineNumber));
        var row = lines[0].Row;
        Assert.Equal((8, "MA/2024/001", "FRA", "Y", (RowProblem?)null), (row.FieldCount, row.AssemblyVoteId, row.CountryCode, row.Ballot, row.LineProblem));
        Assert.Equal(Good, lines[0].RawText);
    }

    [Fact]
    public async Task Columns_are_taken_by_header_name()
    {
        var reordered = "ballot,assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,published_at\nN,MA/2024/001,79,2024-01-12,T,FRA,France,\n";

        var row = (await Read(reordered)).Single().Row;

        Assert.Equal(("N", "MA/2024/001", "FRA"), (row.Ballot, row.AssemblyVoteId, row.CountryCode));
    }

    [Theory]
    // The five cases of the CsvHelper probe. Each damages only its own line; the next good line is read normally.
    [InlineData("MA/2024/001,79,2024-01-12,ab\"c,FRA,France,Y,", "malformed CSV line: a quote inside an unquoted field at position 29")]
    [InlineData("MA/2024/001,79,2024-01-12,\"abc,FRA,France,Y,", "malformed CSV line: a quote opened at position 27 is never closed")]
    [InlineData("MA/2024/001,79,2024-01-12,\"ab\"c,FRA,France,Y,", "malformed CSV line: unexpected text after the closing quote at position 31")]
    [InlineData("MA/2024/001,79,2024-01-12,\"Title with a", "malformed CSV line: a quote opened at position 27 is never closed")]
    public async Task Damaged_quote_makes_only_its_own_line_invalid(string damaged, string message)
    {
        var lines = await Read(File(Good, damaged, Good, Good));

        Assert.Equal([2, 3, 4, 5], lines.Select(l => l.Row.LineNumber));
        var bad = lines[1];
        Assert.Equal(new RowProblem(RowProblemCode.MalformedLine, "line", message, damaged), bad.Row.LineProblem);
        Assert.Equal(damaged, bad.RawText);
        Assert.Null(bad.Row.AssemblyVoteId);
        Assert.All(new[] { lines[0], lines[2], lines[3] }, l => Assert.Null(l.Row.LineProblem));
    }

    [Fact]
    public async Task Line_break_inside_quotes_is_an_unbalanced_quote_on_that_line_and_the_rest_is_its_own_line()
    {
        // The fifth probe case: CsvHelper read this as one record over two lines.
        var lines = await Read(File("MA/2024/001,79,2024-01-12,\"Line one", "line two\",FRA,France,Y,", Good));

        Assert.Equal(RowProblemCode.MalformedLine, lines[0].Row.LineProblem!.Code);
        Assert.Equal(RowProblemCode.MalformedLine, lines[1].Row.LineProblem!.Code); // its own quote is unbalanced too
        Assert.Null(lines[2].Row.LineProblem);
        Assert.Equal(4, lines[2].Row.LineNumber);
    }

    [Fact]
    public async Task Line_of_20000_characters_is_too_long_and_the_next_line_is_read_normally()
    {
        var huge = "MA/2024/001,79,2024-01-12," + new string('x', 20_000 - 26);
        Assert.Equal(20_000, huge.Length);

        var lines = await Read(File(Good, huge, Good));

        Assert.Equal([2, 3, 4], lines.Select(l => l.Row.LineNumber));
        var problem = lines[1].Row.LineProblem!;
        Assert.Equal(RowProblemCode.LineTooLong, problem.Code);
        Assert.Equal("the line is 20,000 characters long, more than the 10,000 allowed; it was skipped", problem.Message);
        Assert.Equal(201, lines[1].RawText.Length); // only the beginning is kept, plus "…"
        Assert.Null(lines[2].Row.LineProblem);
    }

    [Fact]
    public async Task Line_of_exactly_the_limit_is_accepted_also_with_crlf()
    {
        var limit = "MA/2024/001,79,2024-01-12,T,FRA,France,Y," + new string('z', ExportFileReader.MaxLineLength - 41);
        Assert.Equal(ExportFileReader.MaxLineLength, limit.Length);

        var lines = await Read(Header + "\r\n" + limit + "\r\n" + Good + "\r\n");

        Assert.Equal(2, lines.Count);
        Assert.Null(lines[0].Row.LineProblem);
        Assert.Equal(limit, lines[0].RawText);
        Assert.Equal("2024-04-02T09:00:00Z", lines[1].Row.PublishedAt); // no trailing '\r'
    }

    [Fact]
    public async Task Bom_is_ignored_and_a_missing_last_newline_is_fine()
    {
        var lines = await Read(Header + "\n" + Good, bom: true);

        Assert.Equal(Good, Assert.Single(lines).RawText);
    }

    [Fact]
    public async Task Empty_line_is_invalid_not_skipped()
    {
        var lines = await Read(File(Good, "", Good));

        Assert.Equal([2, 3, 4], lines.Select(l => l.Row.LineNumber));
        Assert.Equal("the line is empty", lines[1].Row.LineProblem!.Message);
    }

    [Fact]
    public async Task Wrong_column_count_is_left_to_the_row_rules()
    {
        // Real: historic line 134746 (7 columns).
        var row = (await Read(File("MA/2020/350,75,2020-07-16,Biological Diversity: Follow-up to the Summit,LVA,,Y"))).Single().Row;

        Assert.Equal((7, (RowProblem?)null, (string?)null), (row.FieldCount, row.LineProblem, row.PublishedAt));
    }

    [Theory]
    [InlineData("", "The file is empty: there is no header line. No row was processed.")]
    [InlineData("assembly_vote_id,\"session\n", "Line 1 (the header) cannot be read: a quote opened at position 18 is never closed. No row was processed.")]
    public async Task Only_an_empty_file_or_an_unreadable_header_fails_the_file(string content, string message)
    {
        var ex = await Assert.ThrowsAsync<ImportFileException>(() => Read(content));

        Assert.Equal(message, ex.Message);
    }
}
