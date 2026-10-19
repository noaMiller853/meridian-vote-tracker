using Meridian.Domain.Ingestion;

namespace Meridian.Tests.Domain;

public class CsvLineTests
{
    private static List<string> Split(string line)
    {
        Assert.True(CsvLine.TrySplit(line, out var fields, out var error), error);
        return fields;
    }

    private static string Error(string line)
    {
        Assert.False(CsvLine.TrySplit(line, out _, out var error));
        return error!;
    }

    [Fact]
    public void Plain_fields() => Assert.Equal(["a", "b", "c"], Split("a,b,c"));

    [Fact]
    public void Empty_fields_and_a_trailing_comma_are_kept()
    {
        Assert.Equal(["", "b", ""], Split(",b,"));
        Assert.Equal([""], Split(""));
    }

    [Fact]
    public void Quoted_field_may_contain_commas()
    {
        // Real: q1 line 2.
        Assert.Equal(
            ["MA/2024/001", "79", "2024-01-12", "Desertification, Including Capacity Building", "AFG", "Afghanistan", "N", "2024-04-02T09:00:00Z"],
            Split("MA/2024/001,79,2024-01-12,\"Desertification, Including Capacity Building\",AFG,Afghanistan,N,2024-04-02T09:00:00Z"));
    }

    [Fact]
    public void Doubled_quote_inside_a_quoted_field_is_one_quote()
    {
        // Real: q1 and rev2, vote MA/2024/036.
        Assert.Equal(
            ["MA/2024/036", "Follow-up to the \"Blue Ocean\" Declaration, Including Capacity Building", "AFG"],
            Split("MA/2024/036,\"Follow-up to the \"\"Blue Ocean\"\" Declaration, Including Capacity Building\",AFG"));
        Assert.Equal(["", "x"], Split("\"\",x"));
    }

    [Fact]
    public void Spaces_are_kept_as_they_are()
    {
        Assert.Equal(["MA/2024/001", " fra ", "y"], Split("MA/2024/001, fra ,y"));
    }

    [Theory]
    // The cases from the CsvHelper probe, now each limited to its own line:
    [InlineData("1,ab\"c,x", "a quote inside an unquoted field at position 5")]                   // stray quote mid-field
    [InlineData("1,\"abc,x", "a quote opened at position 3 is never closed")]                    // opening quote never closed
    [InlineData("1,\"ab\"c,x", "unexpected text after the closing quote at position 7")]         // CsvHelper silently made this "abc"
    [InlineData("1,\"line1", "a quote opened at position 3 is never closed")]                    // a line break inside quotes
    [InlineData("\"", "a quote opened at position 1 is never closed")]
    [InlineData("a,\"b\" ,c", "unexpected text after the closing quote at position 6")]
    public void Malformed_quotes_are_reported_never_repaired(string line, string error)
    {
        Assert.Equal(error, Error(line));
    }
}
