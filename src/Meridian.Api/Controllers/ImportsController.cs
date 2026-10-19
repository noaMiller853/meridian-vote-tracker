using System.Text.Json;
using Meridian.Api.Http;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Controllers;

[ApiController]
[Route("api/imports")]
public sealed class ImportsController(MeridianDbContext db, CurrentUser user) : ControllerBase
{
    /// <summary>The historic file is about 100 MB.</summary>
    private const long MaxUploadBytes = 250L * 1024 * 1024;

    /// <summary>Uploads an Assembly export. Returns at once (ING-01); the file is processed in the background.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType<ImportCreatedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Upload(IFormFile? file, [FromServices] ImportService imports, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "No file",
                detail: "Send a non-empty CSV file in the multipart form field 'file'.");

        // Analyst and Admin may both upload (spec section 3).
        await using var content = file.OpenReadStream();
        var created = await imports.CreateAsync(content, file.FileName, user.Id, HttpContext.GetCorrelationId(), ct);

        return AcceptedAtAction(nameof(Get), new { id = created.ImportId },
            new ImportCreatedResponse(created.ImportId, created.JobId, BackgroundJob.StatusQueued));
    }

    /// <summary>One import with its live progress (ING-02); poll it while it runs.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ImportDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var summary = await Summaries(db.Imports.Where(i => i.Id == id)).SingleOrDefaultAsync(ct);
        if (summary is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Import not found", detail: $"There is no import {id}.");

        var reportJson = await db.Imports.Where(i => i.Id == id).Select(i => i.Report).SingleAsync(ct);
        using var report = reportJson is null ? null : JsonDocument.Parse(reportJson);
        return Ok(new ImportDetails(summary, report?.RootElement.Clone()));
    }

    /// <summary>Import history, newest first. Never loads the reports.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ImportSummary>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<ImportSummary>> List([FromQuery] int take = 50, CancellationToken ct = default) =>
        await Summaries(db.Imports.OrderByDescending(i => i.Id).Take(Math.Clamp(take, 1, 500))).ToListAsync(ct);

    // Filter and sort on the entity first: EF cannot translate a filter on the constructed records.
    // The projection does not select imports.report, so listing stays small however large the reports are.
    private static IQueryable<ImportSummary> Summaries(IQueryable<Import> imports) =>
        imports.AsNoTracking().Select(i =>
            new ImportSummary(
                i.Id,
                i.Kind,
                i.FileName,
                i.Kind == Import.KindMemberStates ? i.FileName + " (seed)" : i.FileName,
                i.Sha256,
                i.UploadedBy.Name,
                i.UploadedAt,
                i.PublishedAt,
                // The seed load runs at startup without a background job.
                i.BackgroundJob == null ? BackgroundJob.StatusSucceeded : i.BackgroundJob.Status,
                i.BackgroundJob == null ? null : i.BackgroundJob.Phase,
                i.BackgroundJob == null ? 0 : i.BackgroundJob.Done,
                i.BackgroundJob == null ? null : i.BackgroundJob.Total,
                i.BackgroundJob == null ? null : i.BackgroundJob.Error,
                i.BackgroundJob == null ? null : i.BackgroundJob.FinishedAt,
                new ImportCounts(i.NewCount, i.UnchangedCount, i.DuplicateCount, i.InvalidCount, i.ContradictionCount,
                    i.ConflictCount, i.NormalizedCount)));
}

public sealed record ImportCreatedResponse(long ImportId, long JobId, string Status);

public sealed record ImportCounts(int New, int Unchanged, int Duplicate, int Invalid, int Contradiction, int Conflict, int Normalized);

public sealed record ImportSummary(
    long Id,
    string Kind,
    string FileName,
    string Label,
    string Sha256,
    string UploadedBy,
    DateTimeOffset UploadedAt,
    DateTimeOffset? PublishedAt,
    string Status,
    string? Phase,
    int Done,
    int? Total,
    string? Error,
    DateTimeOffset? FinishedAt,
    ImportCounts Counts);

public sealed record ImportDetails(ImportSummary Import, JsonElement? Report);
