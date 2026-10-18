namespace Meridian.Infrastructure.Persistence.Entities;

public class Vote
{
    public long Id { get; set; }
    public string AssemblyVoteId { get; set; } = null!;
    public int Session { get; set; }
    public DateOnly VoteDate { get; set; }
    public string Title { get; set; } = null!;
    public long FirstImportId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Import FirstImport { get; set; } = null!;
    public VotePosition? Position { get; set; }
    public List<CountryVote> CountryVotes { get; set; } = [];
    public List<Conflict> Conflicts { get; set; } = [];
}
