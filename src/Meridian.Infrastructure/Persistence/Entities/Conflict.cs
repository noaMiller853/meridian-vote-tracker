namespace Meridian.Infrastructure.Persistence.Entities;

public class Conflict
{
    public long Id { get; set; }
    public long VoteId { get; set; }

    /// <summary>CountryVote or VoteField.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Set only for CountryVote conflicts.</summary>
    public string? CountryCode { get; set; }

    /// <summary>Set only for VoteField conflicts: session, vote_date or title.</summary>
    public string? FieldName { get; set; }

    public string ApprovedValue { get; set; } = null!;
    public string ProposedValue { get; set; } = null!;

    public long ApprovedSourceImportId { get; set; }
    public long ProposedSourceImportId { get; set; }
    public int ProposedSourceRowNumber { get; set; }

    /// <summary>Pending, Approved or Rejected.</summary>
    public string Status { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
    public long? DecidedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    public Vote Vote { get; set; } = null!;
    public Country? Country { get; set; }
    public Import ApprovedSourceImport { get; set; } = null!;
    public ImportRow ProposedSourceRow { get; set; } = null!;
    public User? DecidedBy { get; set; }
}
