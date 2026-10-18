namespace Meridian.Infrastructure.Persistence.Entities;

public class AuditEvent
{
    public long Id { get; set; }
    public string Entity { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public long? VoteId { get; set; }
    public string Action { get; set; } = null!;

    /// <summary>jsonb.</summary>
    public string? OldValue { get; set; }

    /// <summary>jsonb.</summary>
    public string? NewValue { get; set; }

    public long ActorId { get; set; }
    public long? ImportId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public Vote? Vote { get; set; }
    public User Actor { get; set; } = null!;
    public Import? Import { get; set; }
}
