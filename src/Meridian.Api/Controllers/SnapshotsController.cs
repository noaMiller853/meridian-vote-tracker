using System.Globalization;
using Meridian.Api.Http;
using Meridian.Domain.Snapshots;
using Meridian.Infrastructure.Snapshots;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Meridian.Api.Controllers;

/// <summary>
/// Snapshots of the Alignment Index (spec 7.6, SNP-01..SNP-05). Everyone reads; analysts and the admin create; only the
/// admin publishes. A saved snapshot is never recalculated (SNP-04).
/// </summary>
[ApiController]
[Route("api/snapshots")]
public sealed class SnapshotsController(SnapshotReader reader, CurrentUser user, ProblemDetailsFactory problems) : ControllerBase
{
    private const int MaxTake = 500;

    public const string VotesWithoutPositionType = "urn:meridian:problem:snapshot-votes-without-position";
    public const string QuarterTakenType = "urn:meridian:problem:snapshot-quarter-published";
    public const string NotSavedType = "urn:meridian:problem:snapshot-not-saved";

    /// <summary>Requests a snapshot. Returns 202 at once; the calculation runs in the background (SNP-02).</summary>
    [HttpPost]
    [EndpointSummary("Create a snapshot from a selection of votes (Analyst or Admin)")]
    [EndpointDescription("""
        Selection (SNP-01): dateFrom and/or dateTo (inclusive), session, and/or assemblyVoteIds. A filter and a list are
        combined as a union: the votes that match the filter plus the listed ones. An unknown id is a 400 under
        assemblyVoteIds.

        Votes without a position (POS-04): by default the request is refused with 409, listing them, and nothing is
        created. Set their positions, remove them from the selection, or resend with "excludeVotesWithoutPosition": true;
        the snapshot then lists them as excluded with the reason.

        Example: {"name": "2024 Q1", "dateFrom": "2024-01-01", "dateTo": "2024-03-31"}. Poll GET /api/snapshots/{id} for
        the status and progress.
        """)]
    [ProducesResponseType<SnapshotCreatedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateSnapshotBody body, [FromServices] SnapshotService snapshots, CancellationToken ct)
    {
        if (!user.CanCreateSnapshots)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed",
                detail: $"{user.Name} ({user.Role}) cannot create snapshots. Analysts and the admin can.");

        var selection = new SnapshotSelection(body.DateFrom, body.DateTo, body.Session, body.AssemblyVoteIds ?? []);
        foreach (var error in selection.Validate(body.Name))
            ModelState.AddModelError(error.Field, error.Message);
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var result = await snapshots.CreateAsync(body.Name!, selection, body.ExcludeVotesWithoutPosition ?? false, user.Id,
            HttpContext.GetCorrelationId(), ct);
        switch (result.Kind)
        {
            case CreateResultKind.Created:
                return AcceptedAtAction(nameof(Get), new { id = result.SnapshotId },
                    new SnapshotCreatedResponse(result.SnapshotId, result.JobId, result.Count));
            case CreateResultKind.UnknownVotes:
                ModelState.AddModelError(SnapshotSelection.Fields.AssemblyVoteIds,
                    $"{result.Count} vote id(s) do not exist: {string.Join(", ", result.Votes!)}{(result.Count > result.Votes!.Count ? ", …" : "")}.");
                return ValidationProblem(ModelState);
            case CreateResultKind.NoVotes:
                ModelState.AddModelError(body.AssemblyVoteIds is { Count: > 0 } ? SnapshotSelection.Fields.AssemblyVoteIds
                    : body.Session is not null ? SnapshotSelection.Fields.Session : SnapshotSelection.Fields.DateFrom,
                    "The selection matches no vote.");
                return ValidationProblem(ModelState);
            default:
                var problem = problems.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict, "Votes without a position",
                    VotesWithoutPositionType,
                    $"{result.Count} selected vote(s) have no position, so they would not count in the snapshot (POS-04). " +
                    "Set their positions, remove them from the selection, or resend with \"excludeVotesWithoutPosition\": true " +
                    "to create the snapshot with them listed as excluded. Nothing was created.");
                problem.Extensions["votesWithoutPosition"] = new VotesWithoutPosition(result.Count, result.Votes!);
                return Problem409(problem);
        }
    }

    /// <summary>Snapshots, newest first, with status and progress (SNP-02).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SnapshotSummary>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<SnapshotSummary>> List([FromQuery] int take = 50, CancellationToken ct = default) =>
        await reader.ListAsync(Math.Clamp(take, 1, MaxTake), ct);

    /// <summary>
    /// One snapshot: status and progress while it runs; then the rules used, the selection, the excluded votes with their
    /// reason (POS-04) and the ranking of every listed country with its score (SNP-03).
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<SnapshotDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        await reader.GetAsync(id, ct) is { } details ? Ok(details) : SnapshotNotFound(id);

    /// <summary>How one country's score was calculated: the contribution of every included vote, paged by ordinal.</summary>
    [HttpGet("{id:long}/countries/{countryCode}")]
    [ProducesResponseType<CountryBreakdown>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Country(long id, string countryCode, [FromQuery] int afterOrdinal = -1, [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var code = countryCode.Trim().ToUpperInvariant();
        if (await reader.CountryAsync(id, code, afterOrdinal, Math.Clamp(take, 1, MaxTake), ct) is { } breakdown)
            return Ok(breakdown);
        return await reader.ExistsAsync(id, ct)
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Country not in the snapshot",
                detail: $"Snapshot {id} has no result for {code}: it is not calculated yet, or {code} was not a member on the date of any included vote.")
            : SnapshotNotFound(id);
    }

    /// <summary>The frozen votes of a snapshot (<c>included=true|false</c>), paged by vote id.</summary>
    [HttpGet("{id:long}/votes")]
    [ProducesResponseType<SnapshotVotePage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Votes(long id, [FromQuery] bool? included, [FromQuery] long afterVoteId = 0, [FromQuery] int take = 100,
        CancellationToken ct = default) =>
        await reader.ExistsAsync(id, ct)
            ? Ok(await reader.VotesAsync(id, included, afterVoteId, Math.Clamp(take, 1, MaxTake), ct))
            : SnapshotNotFound(id);

    /// <summary>Publishes a saved snapshot for its quarter (SNP-05). Admin only.</summary>
    [HttpPut("{id:long}/publish")]
    [EndpointSummary("Publish a snapshot for its quarter (Admin only)")]
    [EndpointDescription("""
        Publishes a Saved snapshot for its quarter: the calendar quarter of its latest included vote date (shown as
        "quarter"). One snapshot is published per quarter. If the quarter already has one, the answer is 409 naming it;
        resend with {"replace": true} to publish this one instead: the other returns to Saved in the same transaction and
        both changes are audited.
        """)]
    [ProducesResponseType<SnapshotDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(long id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PublishBody? body, [FromServices] SnapshotService snapshots, CancellationToken ct)
    {
        if (!user.CanPublishSnapshots)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed",
                detail: $"{user.Name} ({user.Role}) cannot publish snapshots. Only the admin can (SNP-05).");

        var result = await snapshots.PublishAsync(id, body?.Replace ?? false, user.Id, HttpContext.GetCorrelationId(), ct);
        switch (result.Kind)
        {
            case PublishResultKind.Published:
                return Ok(result.Snapshot);
            case PublishResultKind.NotFound:
                return SnapshotNotFound(id);
            case PublishResultKind.NotSaved:
            {
                var status = result.Snapshot!.Snapshot.Status;
                var problem = problems.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict, "Snapshot cannot be published",
                    NotSavedType, status == "Published"
                        ? $"Snapshot {id} is already published for {result.Snapshot.Snapshot.Quarter}."
                        : $"Snapshot {id} is {status}; only a Saved snapshot can be published.");
                problem.Extensions["snapshot"] = result.Snapshot.Snapshot;
                return Problem409(problem);
            }
            default:
            {
                var current = result.Current;
                var detail = current is null
                    ? $"Another snapshot was published for {result.Snapshot!.Snapshot.Quarter} at the same moment. Nothing was changed."
                    : $"Snapshot {current.Id} (\"{current.Name}\") is already published for {current.Quarter}, by {current.PublishedBy} at " +
                      $"{current.PublishedAt!.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}. Only one snapshot " +
                      "is published per quarter (SNP-05). Nothing was changed. To publish this one instead, resend with {\"replace\": true}.";
                var problem = problems.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict, "Quarter already published",
                    QuarterTakenType, detail);
                problem.Extensions["published"] = current;
                return Problem409(problem);
            }
        }
    }

    private static ObjectResult Problem409(ProblemDetails problem) =>
        new(problem) { StatusCode = StatusCodes.Status409Conflict, ContentTypes = { "application/problem+json" } };

    private ObjectResult SnapshotNotFound(long id) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Snapshot not found", detail: $"There is no snapshot {id}.");
}

/// <param name="DateFrom">Inclusive, yyyy-mm-dd.</param>
/// <param name="DateTo">Inclusive, yyyy-mm-dd.</param>
/// <param name="AssemblyVoteIds">Votes added to the filter's votes (a union).</param>
/// <param name="ExcludeVotesWithoutPosition">Confirms that selected votes without a position are left out and listed (POS-04).</param>
public sealed record CreateSnapshotBody(string? Name, DateOnly? DateFrom, DateOnly? DateTo, int? Session, List<string>? AssemblyVoteIds,
    bool? ExcludeVotesWithoutPosition);

/// <param name="Replace">Publish this one even if the quarter already has a published snapshot, which returns to Saved.</param>
public sealed record PublishBody(bool? Replace);

/// <param name="SelectedVotes">Votes in the selection when it was requested.</param>
public sealed record SnapshotCreatedResponse(long SnapshotId, long JobId, int SelectedVotes);

/// <param name="AssemblyVoteIds">The first 50.</param>
public sealed record VotesWithoutPosition(int Count, IReadOnlyList<string> AssemblyVoteIds);
