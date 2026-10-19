using System.Globalization;
using Meridian.Api.Http;
using Meridian.Domain.Review;
using Meridian.Infrastructure.Review;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Meridian.Api.Controllers;

/// <summary>
/// The review queue (CON-01..CON-06). Conflicts are opened by imports; an analyst or the admin approves or rejects them.
/// Decided conflicts stay readable with their decision.
/// </summary>
// No [Produces("application/json")]: it would also force that type onto the ProblemDetails (application/problem+json).
[ApiController]
[Route("api/conflicts")]
public sealed class ConflictsController(ConflictReader reader, CurrentUser user, ProblemDetailsFactory problems) : ControllerBase
{
    private const int MaxTake = 500;
    private const int MaxNoteLength = 1000;
    private const string AllStatuses = "All";

    public const string AlreadyDecidedType = "urn:meridian:problem:conflict-already-decided";
    public const string StaleType = "urn:meridian:problem:conflict-stale";

    /// <summary>
    /// Conflicts, oldest first. Default: the ones waiting for a decision (<c>status=Pending</c>); <c>status=All</c> for every
    /// status. Filter by vote with its Assembly id (<c>MA/2024/012</c>). Paged by id: pass <c>nextAfterId</c> as <c>afterId</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ConflictPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? assemblyVoteId, [FromQuery] string? kind,
        [FromQuery] long afterId = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        status = string.IsNullOrWhiteSpace(status) ? ConflictDecision.Pending : status.Trim();
        if (status != AllStatuses && !ConflictReader.Statuses.Contains(status))
            ModelState.AddModelError(nameof(status), $"Unknown status '{status}'. Use {string.Join(", ", ConflictReader.Statuses)} or {AllStatuses}.");
        if (!string.IsNullOrWhiteSpace(kind) && !ConflictReader.Kinds.Contains(kind.Trim()))
            ModelState.AddModelError(nameof(kind), $"Unknown kind '{kind}'. Use {string.Join(" or ", ConflictReader.Kinds)}.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var filter = new ConflictFilter(
            status == AllStatuses ? null : status,
            string.IsNullOrWhiteSpace(assemblyVoteId) ? null : assemblyVoteId.Trim(),
            string.IsNullOrWhiteSpace(kind) ? null : kind.Trim(),
            afterId,
            Math.Clamp(take, 1, MaxTake));
        return Ok(await reader.ListAsync(filter, ct));
    }

    /// <summary>
    /// One conflict: approved value and new value side by side with the source of each (file, uploaded by, when), the
    /// approved value now (<c>current.isStale</c>) and the decision if there is one.
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ConflictDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        await reader.GetAsync(id, ct) is { } details ? Ok(details) : ConflictNotFound(id);

    /// <summary>
    /// Approve (the new value becomes approved) or reject (the approved value stays). Analyst or Admin. Only one decision
    /// is applied: a conflict that is no longer pending gives 409 with who decided, when and what. If the approved value
    /// changed since the conflict was opened, Approve gives 409 until it is resent with <c>expectedApprovedValue</c> set to
    /// the current value; Reject is always possible.
    /// </summary>
    [HttpPost("{id:long}/decision")]
    [ProducesResponseType<ConflictDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decide(long id, DecisionBody body, [FromServices] ConflictDecisionService decisions, CancellationToken ct)
    {
        if (!user.CanDecideConflicts)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed",
                detail: $"{user.Name} ({user.Role}) cannot decide conflicts. Analysts and the admin can.");

        // By name only: Enum.TryParse would also accept numbers such as "0".
        var name = Enum.GetNames<Decision>().FirstOrDefault(n => n.Equals(body.Decision?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(body.Decision))
            ModelState.AddModelError("decision", "The decision is required: Approve or Reject.");
        else if (name is null)
            ModelState.AddModelError("decision", $"Unknown decision '{body.Decision}'. Use Approve or Reject.");
        if (body.Note is { Length: > MaxNoteLength })
            ModelState.AddModelError("note", $"The note is longer than {MaxNoteLength} characters.");
        if (body.ExpectedApprovedValue is { Length: > MaxNoteLength })
            ModelState.AddModelError("expectedApprovedValue", $"The value is longer than {MaxNoteLength} characters.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var decision = Enum.Parse<Decision>(name!);
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        var result = await decisions.DecideAsync(id, new DecisionRequest(decision, note, body.ExpectedApprovedValue),
            user.Id, HttpContext.GetCorrelationId(), ct);

        return result.Kind switch
        {
            DecisionResultKind.Applied => Ok(result.Conflict),
            DecisionResultKind.NotFound => ConflictNotFound(id),
            DecisionResultKind.AlreadyDecided => Conflict409(AlreadyDecidedType, "Conflict already decided",
                AlreadyDecidedDetail(result.Conflict!, decision), result.Conflict!),
            _ => Conflict409(StaleType, "Approved value changed",
                StaleDetail(result.Conflict!, body.ExpectedApprovedValue), result.Conflict!),
        };
    }

    private static string AlreadyDecidedDetail(ConflictDetails c, Decision attempted)
    {
        var d = c.Decision!;
        var note = d.Note is null ? "" : $" with the note \"{d.Note}\"";
        return $"Conflict {c.Id} was already {d.Decision.ToLowerInvariant()} by {d.DecidedBy} at " +
               $"{d.DecidedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}{note}. " +
               $"Your decision ({attempted}) was not applied. The conflict below is its current state.";
    }

    private static string StaleDetail(ConflictDetails c, string? expected)
    {
        var now = c.Current.Value;
        var by = c.Current.ChangedBy is { } change
            ? $" by conflict {change.ConflictId}, approved by {change.DecidedBy} at " +
              change.DecidedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : "";
        var start = expected is null
            ? $"The approved value is now '{now}'; it was '{c.Approved.Value}' when this conflict was opened (changed{by})."
            : $"The approved value is now '{now}', not '{expected}' (changed{by}).";
        return $"{start} Nothing was changed. To approve '{c.Proposed.Value}' anyway, resend with expectedApprovedValue " +
               $"'{now}'. Rejecting is always possible.";
    }

    private ObjectResult Conflict409(string type, string title, string detail, ConflictDetails current)
    {
        var problem = problems.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict, title, type, detail);
        problem.Extensions["conflict"] = current;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict, ContentTypes = { "application/problem+json" } };
    }

    private ObjectResult ConflictNotFound(long id) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Conflict not found", detail: $"There is no conflict {id}.");
}

/// <param name="Decision">Approve or Reject.</param>
/// <param name="Note">Optional, at most 1000 characters (CON-04).</param>
/// <param name="ExpectedApprovedValue">
/// Only to approve a stale conflict on purpose: the approved value as the analyst saw it (<c>current.value</c>).
/// </param>
public sealed record DecisionBody(string? Decision, string? Note, string? ExpectedApprovedValue);
