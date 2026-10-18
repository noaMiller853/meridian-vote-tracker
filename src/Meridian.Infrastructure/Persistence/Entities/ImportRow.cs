namespace Meridian.Infrastructure.Persistence.Entities;

/// <summary>
/// One line of an imported file. Parsed fields are kept as text because Invalid rows cannot be typed.
/// Written with bulk COPY, never row by row through EF.
/// </summary>
public class ImportRow
{
    public long ImportId { get; set; }
    public int RowNumber { get; set; }
    public string RawLine { get; set; } = null!;

    public string? AssemblyVoteId { get; set; }
    public string? Session { get; set; }
    public string? VoteDate { get; set; }
    public string? Title { get; set; }

    /// <summary>Country code after trim and upper-case (ING-13).</summary>
    public string? CountryCode { get; set; }

    /// <summary>Country code exactly as it appeared in the file.</summary>
    public string? OriginalCountryCode { get; set; }

    public string? CountryName { get; set; }
    public string? Ballot { get; set; }
    public string? PublishedAt { get; set; }

    public bool Normalized { get; set; }

    /// <summary>New, Unchanged, Duplicate, Invalid, Contradiction or Conflict.</summary>
    public string Classification { get; set; } = null!;

    public string? Reason { get; set; }

    /// <summary>The value the reason refers to, as found in the file.</summary>
    public string? OriginalValue { get; set; }

    public Import Import { get; set; } = null!;
}
