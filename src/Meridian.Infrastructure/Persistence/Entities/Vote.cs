namespace Meridian.Infrastructure.Persistence.Entities;

public class Vote
{
    public long Id { get; set; }
    public string AssemblyVoteId { get; set; } = null!;
    public int Session { get; set; }
    public DateOnly VoteDate { get; set; }
    public string Title { get; set; } = null!;
    public long FirstImportId { get; set; }

    // The import that set each detail: the first import, or the proposing import of an approved vote-field conflict.
    // It is the approved source shown on a later conflict on that field (CON-02).
    public long SessionSourceImportId { get; set; }
    public long VoteDateSourceImportId { get; set; }
    public long TitleSourceImportId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Import FirstImport { get; set; } = null!;
    public Import SessionSourceImport { get; set; } = null!;
    public Import VoteDateSourceImport { get; set; } = null!;
    public Import TitleSourceImport { get; set; } = null!;
    public VotePosition? Position { get; set; }
    public List<CountryVote> CountryVotes { get; set; } = [];
    public List<Conflict> Conflicts { get; set; } = [];
}
