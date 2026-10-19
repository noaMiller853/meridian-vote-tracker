using System.Runtime.CompilerServices;
using System.Text;
using Meridian.Domain.Ingestion;

namespace Meridian.Infrastructure.Imports;

/// <summary>One physical line as read, with the row built from it.</summary>
/// <param name="RawText">The line as found (for a line that is too long: only its beginning).</param>
public sealed record ExportLine(RawRow Row, string RawText);

/// <summary>
/// Reads an Assembly export one physical line at a time (the format has no line breaks inside a field), so a damaged
/// quote can never swallow the following lines: it makes only its own line Invalid (ING-04, ING-07). Lines are bounded:
/// a line longer than <see cref="MaxLineLength"/> is reported as too long and the rest of it is skipped without being
/// buffered. Only a missing or unusable header fails the whole file.
/// </summary>
public static class ExportFileReader
{
    public const int MaxLineLength = 10_000;

    /// <summary>How much of a too-long line is kept as its raw text.</summary>
    private const int TooLongPreview = 200;

    public static async IAsyncEnumerable<ExportLine> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        await using var lines = ReadLinesAsync(reader, MaxLineLength, ct).GetAsyncEnumerator(ct);

        if (!await lines.MoveNextAsync())
            throw new ImportFileException("The file is empty: there is no header line. No row was processed.");
        var columns = ReadHeader(lines.Current);

        while (await lines.MoveNextAsync())
            yield return ToRow(lines.Current, columns);
    }

    /// <summary>Physical lines (LF or CRLF), numbered from 1, each at most <paramref name="maxLength"/> characters.</summary>
    public static async IAsyncEnumerable<PhysicalLine> ReadLinesAsync(TextReader reader, int maxLength,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new char[1 << 16];
        var line = new StringBuilder();
        var number = 0;
        long length = 0;        // characters on the current line, including the ones not kept
        var pending = false;    // the current line has started

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, '\n', start, read - start);
                var end = newline < 0 ? read : newline;
                var count = end - start;
                if (count > 0)
                {
                    // Keep one character more than the limit, so a trailing '\r' can still be removed.
                    var room = maxLength + 1 - line.Length;
                    if (room > 0)
                        line.Append(buffer, start, Math.Min(count, room));
                    length += count;
                    pending = true;
                }

                if (newline < 0)
                    break;

                yield return Complete(++number, line, length, maxLength);
                line.Clear();
                length = 0;
                pending = false;
                start = newline + 1;
            }
        }

        if (pending)
            yield return Complete(++number, line, length, maxLength);
    }

    private static PhysicalLine Complete(int number, StringBuilder line, long length, int maxLength)
    {
        if (line.Length > 0 && line[^1] == '\r' && length == line.Length)
        {
            line.Length--;
            length--;
        }
        return length > maxLength
            ? new PhysicalLine(number, line.ToString(0, Math.Min(line.Length, TooLongPreview)), length, TooLong: true)
            : new PhysicalLine(number, line.ToString(), length, TooLong: false);
    }

    private static Dictionary<string, int> ReadHeader(PhysicalLine header)
    {
        if (header.TooLong)
            throw new ImportFileException("Line 1 (the header) cannot be read: it is too long. No row was processed.");
        if (!CsvLine.TrySplit(header.Text, out var names, out var error))
            throw new ImportFileException($"Line 1 (the header) cannot be read: {error}. No row was processed.");

        var check = ExportColumns.CheckHeader(names);
        if (!check.IsValid)
            throw new ImportFileException(check.Message, new { check.Missing, check.Unknown, check.Repeated, header = names });

        return names.Select((name, index) => (name, index)).ToDictionary(c => c.name, c => c.index);
    }

    private static ExportLine ToRow(PhysicalLine line, Dictionary<string, int> columns)
    {
        if (line.TooLong)
            return Problem(line, RowProblemCode.LineTooLong,
                $"the line is {line.Length:N0} characters long, more than the {MaxLineLength:N0} allowed; it was skipped", line.Text + "…");

        if (string.IsNullOrWhiteSpace(line.Text))
            return Problem(line, RowProblemCode.MalformedLine, "the line is empty", line.Text);

        if (!CsvLine.TrySplit(line.Text, out var fields, out var error))
            return Problem(line, RowProblemCode.MalformedLine, $"malformed CSV line: {error}", line.Text);

        string? Field(string column) => columns[column] < fields.Count ? fields[columns[column]] : null;
        var row = new RawRow(line.Number, fields.Count,
            Field(ExportColumns.AssemblyVoteId), Field(ExportColumns.Session), Field(ExportColumns.VoteDate), Field(ExportColumns.Title),
            Field(ExportColumns.CountryCode), Field(ExportColumns.CountryName), Field(ExportColumns.Ballot), Field(ExportColumns.PublishedAt));
        return new ExportLine(row, line.Text);
    }

    private static ExportLine Problem(PhysicalLine line, RowProblemCode code, string message, string raw) =>
        new(new RawRow(line.Number, 0, null, null, null, null, null, null, null, null,
            new RowProblem(code, "line", message, raw)), raw);
}

/// <param name="Length">Characters on the line, including the part not kept when it is too long.</param>
public sealed record PhysicalLine(int Number, string Text, long Length, bool TooLong);
