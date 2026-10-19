using System.Globalization;
using Meridian.Api.Http;
using Meridian.Domain.Positions;
using Meridian.Infrastructure.Votes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Meridian.Api.Controllers;

/// <summary>
/// The Votes screens (section 6): list, one vote with its country votes and position, the timeline (HIS-04), and the
/// position and weight (POS-01..POS-04). Everyone reads; only the Admin sets positions.
/// </summary>
[ApiController]
[Route("api/votes")]
public sealed class VotesController(VoteReader reader, CurrentUser user, ProblemDetailsFactory problems) : ControllerBase
{
    private const int MaxTake = 500;

    public const string PositionChangedType = "urn:meridian:problem:position-changed";

    /// <summary>
    /// Votes by id. Filters: <c>assemblyVoteId</c> (exact), <c>text</c> (in the title or the Assembly id), <c>session</c>,
    /// <c>dateFrom</c> and <c>dateTo</c> (inclusive, yyyy-mm-dd), <c>hasPosition</c>. A vote without a position has
    /// <c>hasPosition: false</c> and <c>position: null</c> (POS-04). Paged: pass <c>nextAfterId</c> as <c>afterId</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<VotePage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] string? assemblyVoteId, [FromQuery] string? text, [FromQuery] int? session,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, [FromQuery] bool? hasPosition,
        [FromQuery] long afterId = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        if (dateFrom is not null && dateTo is not null && dateFrom > dateTo)
            ModelState.AddModelError(nameof(dateFrom), $"dateFrom {dateFrom:yyyy-MM-dd} is after dateTo {dateTo:yyyy-MM-dd}.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var filter = new VoteFilter(
            string.IsNullOrWhiteSpace(assemblyVoteId) ? null : assemblyVoteId.Trim(),
            string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            session, dateFrom, dateTo, hasPosition, afterId, Math.Clamp(take, 1, MaxTake));
        return Ok(await reader.ListAsync(filter, ct));
    }

    /// <summary>One vote: details with their source, country votes (who approved each, and whether a conflict decision changed it), position and pending conflicts.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<VoteView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        await reader.GetAsync(id, ct) is { } vote ? Ok(vote) : VoteNotFound(id);

    /// <summary>
    /// Everything that happened in the vote, oldest first (HIS-04): every import that contained it, vote created,
    /// countries added, conflicts opened and decided, values changed by a decision, position and weight changes, with
    /// who, when, the source file and the previous and new value.
    /// </summary>
    [HttpGet("{id:long}/timeline")]
    [ProducesResponseType<VoteTimeline>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Timeline(long id, CancellationToken ct) =>
        await reader.TimelineAsync(id, ct) is { } timeline ? Ok(timeline) : VoteNotFound(id);

    /// <summary>Sets the stance and weight of a vote (POS-01). The text below is what Swagger shows.</summary>
    [HttpPut("{id:long}/position")]
    [EndpointSummary("Set the stance and weight of a vote (Admin only)")]
    [EndpointDescription("""
        Sets the stance (For or Against) and the weight (1 to 5) of a vote. Admin only (POS-01): an Analyst gets 403.

        expectedVersion is the position.version you last read from GET /api/votes/{id}. For the FIRST position of a vote
        (position is null), send expectedVersion 0, for example: {"stance": "For", "weight": 4, "expectedVersion": 0}.
        Each change adds 1 to the version.

        If someone changed the position since you read it, the answer is 409 with the current position in "position",
        and nothing is changed. Sending the same stance and weight again returns 200 with "changed": false and records
        nothing. Validation errors come back as 400 under the field name (stance, weight, expectedVersion).
        """)]
    [ProducesResponseType<PositionResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetPosition(long id, PositionBody body, [FromServices] PositionService positions, CancellationToken ct)
    {
        if (!user.CanSetPositions)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed",
                detail: $"{user.Name} ({user.Role}) cannot set positions. Only the admin can (POS-01).");

        var errors = PositionRules.Validate(body.Stance, body.Weight, body.ExpectedVersion, out var position);
        foreach (var error in errors)
            ModelState.AddModelError(error.Field, error.Message);
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var result = await positions.SetAsync(id, position!, body.ExpectedVersion!.Value, user.Id, HttpContext.GetCorrelationId(), ct);
        return result.Kind switch
        {
            PositionResultKind.Changed => Ok(new PositionResult(true, result.Position!)),
            PositionResultKind.Unchanged => Ok(new PositionResult(false, result.Position!)),
            PositionResultKind.NotFound => VoteNotFound(id),
            _ => PositionChanged(id, body.ExpectedVersion.Value, result.Position),
        };
    }

    private ObjectResult PositionChanged(long id, int expected, PositionDto? current)
    {
        var detail = current is null
            ? $"Vote {id} has no position, so expectedVersion must be 0; you sent {expected}. Nothing was changed."
            : $"The position of vote {id} was set by {current.SetBy} at " +
              $"{current.SetAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)} to {current.Stance}, weight " +
              $"{current.Weight} (version {current.Version}); you sent expectedVersion {expected}. Nothing was changed. " +
              $"Check the current position, then resend with expectedVersion {current.Version}.";
        var problem = problems.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict, "Position changed", PositionChangedType, detail);
        problem.Extensions["position"] = current;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict, ContentTypes = { "application/problem+json" } };
    }

    private ObjectResult VoteNotFound(long id) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Vote not found", detail: $"There is no vote {id}.");
}

/// <param name="Stance">For or Against.</param>
/// <param name="Weight">1 (marginal) to 5 (critical).</param>
/// <param name="ExpectedVersion">The position version you last read; 0 when the vote has no position yet.</param>
public sealed record PositionBody(string? Stance, int? Weight, int? ExpectedVersion);
