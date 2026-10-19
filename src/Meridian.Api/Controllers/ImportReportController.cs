using System.Text.Json;
using Meridian.Domain.Ingestion;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Controllers;

/// <summary>
/// The stored import report (ING-14, ING-15): summary, rows and votes. Rows and votes are always paged; no response
/// returns all rows of an import. Line numbers are lines in the file (the header is line 1).
/// </summary>
[ApiController]
[Route("api/imports/{importId:long}")]
public sealed class ImportReportController(MeridianDbContext db) : ControllerBase
{
    private const int MaxTake = 500;
    private const int MaxRowsPerVote = 1000;

    /// <summary>Rows that need attention; the default filter of the rows list.</summary>
    private static readonly string[] Flagged =
        [nameof(RowClassification.Invalid), nameof(RowClassification.Duplicate), nameof(RowClassification.Contradiction), nameof(RowClassification.Conflict)];

    /// <summary>Counts per class and the stored summary: conflicts, normalizations, empty optional fields, votes.</summary>
    [HttpGet("report")]
    [ProducesResponseType<ImportReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Report(long importId, CancellationToken ct)
    {
        var import = await db.Imports.AsNoTracking().Where(i => i.Id == importId)
            .Select(i => new
            {
                i.FileName, i.Report, Status = i.BackgroundJob == null ? BackgroundJob.StatusSucceeded : i.BackgroundJob.Status,
                Counts = new ImportCounts(i.NewCount, i.UnchangedCount, i.DuplicateCount, i.InvalidCount, i.ContradictionCount,
                    i.ConflictCount, i.NormalizedCount),
            })
            .SingleOrDefaultAsync(ct);
        if (import is null)
            return ImportNotFound(importId);

        using var summary = import.Report is null ? null : JsonDocument.Parse(import.Report);
        return Ok(new ImportReport(importId, import.FileName, import.Status, import.Counts, summary?.RootElement.Clone()));
    }

    /// <summary>
    /// Rows of the import with their class, reason and original value. Default: the flagged rows (Invalid, Duplicate,
    /// Contradiction, Conflict); <c>class=All</c> for every row. Paged by line: pass <c>nextAfter</c> as <c>after</c>.
    /// </summary>
    [HttpGet("rows")]
    [ProducesResponseType<ImportRowPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rows(long importId, [FromQuery(Name = "class")] string? classes, [FromQuery] string? vote,
        [FromQuery] int after = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        if (!await db.Imports.AnyAsync(i => i.Id == importId, ct))
            return ImportNotFound(importId);

        string[]? filter = Flagged;
        if (!string.IsNullOrWhiteSpace(classes))
        {
            var requested = classes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requested.Any(c => c.Equals("All", StringComparison.OrdinalIgnoreCase)))
                filter = null;
            else
            {
                var unknown = requested.Where(c => !Enum.TryParse<RowClassification>(c, ignoreCase: false, out _)).ToList();
                if (unknown.Count > 0)
                    return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown class",
                        detail: $"Unknown class {string.Join(", ", unknown)}. Use {string.Join(", ", Enum.GetNames<RowClassification>())} or All.");
                filter = requested;
            }
        }

        var query = db.ImportRows.AsNoTracking().Where(r => r.ImportId == importId && r.RowNumber > after);
        if (filter is not null)
            query = query.Where(r => filter.Contains(r.Classification));
        if (!string.IsNullOrWhiteSpace(vote))
            query = query.Where(r => r.AssemblyVoteId == vote);

        var limit = Math.Clamp(take, 1, MaxTake);
        var rows = await query.OrderBy(r => r.RowNumber).Take(limit).ToListAsync(ct);
        return Ok(new ImportRowPage(rows.Select(ToDto).ToList(), rows.Count == limit ? rows[^1].RowNumber : null));
    }

    /// <summary>What happened in each vote of the import. Paged by vote id: pass <c>nextAfter</c> as <c>after</c>.</summary>
    [HttpGet("votes")]
    [ProducesResponseType<ImportVotePage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Votes(long importId, [FromQuery] string? status, [FromQuery] string? after,
        [FromQuery] int take = 100, CancellationToken ct = default)
    {
        if (!await db.Imports.AnyAsync(i => i.Id == importId, ct))
            return ImportNotFound(importId);

        var query = db.ImportVotes.AsNoTracking().Where(v => v.ImportId == importId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(v => v.Status == status);
        if (!string.IsNullOrEmpty(after))
            query = query.Where(v => string.Compare(v.AssemblyVoteId, after) > 0);

        var limit = Math.Clamp(take, 1, MaxTake);
        var votes = await query.OrderBy(v => v.AssemblyVoteId).Take(limit).ToListAsync(ct);
        return Ok(new ImportVotePage(votes.Select(ToDto).ToList(), votes.Count == limit ? votes[^1].AssemblyVoteId : null));
    }

    /// <summary>One vote of the import with all its rows. The vote id is a query parameter because it contains slashes.</summary>
    [HttpGet("vote")]
    [ProducesResponseType<ImportVoteDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Vote(long importId, [FromQuery] string assemblyVoteId, CancellationToken ct)
    {
        var vote = await db.ImportVotes.AsNoTracking().SingleOrDefaultAsync(v => v.ImportId == importId && v.AssemblyVoteId == assemblyVoteId, ct);
        if (vote is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Vote not found",
                detail: $"Import {importId} has no vote '{assemblyVoteId}'.");

        var rows = await db.ImportRows.AsNoTracking()
            .Where(r => r.ImportId == importId && r.AssemblyVoteId == assemblyVoteId)
            .OrderBy(r => r.RowNumber).Take(MaxRowsPerVote).ToListAsync(ct);
        return Ok(new ImportVoteDetails(ToDto(vote), rows.Select(ToDto).ToList()));
    }

    private IActionResult ImportNotFound(long importId) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Import not found", detail: $"There is no import {importId}.");

    private static ImportRowDto ToDto(ImportRow r) => new(
        r.RowNumber, r.Classification, r.Reason, r.OriginalValue, r.ConflictAction, r.AssemblyVoteId, r.CountryCode,
        r.OriginalCountryCode, r.Ballot, r.RawLine, Parse(r.Problems), Parse(r.Normalizations));

    private static ImportVoteDto ToDto(ImportVote v) => new(
        v.AssemblyVoteId, v.VoteId, v.Status, v.VoteCreated, v.RowCount,
        new ImportVoteCounts(v.NewCount, v.UnchangedCount, v.DuplicateCount, v.InvalidCount, v.ContradictionCount, v.ConflictCount),
        v.ConflictsOpened, v.ConflictsAlreadyPending, v.ConflictsAlreadyRejected, v.ConflictsAlreadySuperseded, Parse(v.FieldConflicts), v.ContradictionReason);

    private static JsonElement? Parse(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public sealed record ImportReport(long ImportId, string FileName, string Status, ImportCounts Counts, JsonElement? Summary);

/// <param name="Line">Line in the file (the header is line 1).</param>
public sealed record ImportRowDto(
    int Line,
    string Classification,
    string? Reason,
    string? OriginalValue,
    string? ConflictAction,
    string? AssemblyVoteId,
    string? CountryCode,
    string? OriginalCountryCode,
    string? Ballot,
    string RawLine,
    JsonElement? Problems,
    JsonElement? Normalizations);

/// <param name="NextAfter">Pass as <c>after</c> for the next page; null on the last page.</param>
public sealed record ImportRowPage(IReadOnlyList<ImportRowDto> Items, int? NextAfter);

public sealed record ImportVoteCounts(int New, int Unchanged, int Duplicate, int Invalid, int Contradiction, int Conflict);

public sealed record ImportVoteDto(
    string AssemblyVoteId,
    long? VoteId,
    string Status,
    bool VoteCreated,
    int Rows,
    ImportVoteCounts Counts,
    int ConflictsOpened,
    int ConflictsAlreadyPending,
    int ConflictsAlreadyRejected,
    int ConflictsAlreadySuperseded,
    JsonElement? FieldConflicts,
    string? ContradictionReason);

public sealed record ImportVotePage(IReadOnlyList<ImportVoteDto> Items, string? NextAfter);

public sealed record ImportVoteDetails(ImportVoteDto Vote, IReadOnlyList<ImportRowDto> Rows);
