namespace Meridian.Infrastructure.Persistence.Entities;

public class CountryVote
{
    public long VoteId { get; set; }
    public string CountryCode { get; set; } = null!;

    /// <summary>Y, N, A or X.</summary>
    public string Ballot { get; set; } = null!;

    public long SourceImportId { get; set; }
    public int SourceRowNumber { get; set; }
    public long ApprovedById { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }

    public Vote Vote { get; set; } = null!;
    public Country Country { get; set; } = null!;
    public ImportRow SourceRow { get; set; } = null!;
    public User ApprovedBy { get; set; } = null!;
}
