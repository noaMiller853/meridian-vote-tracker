using System.Text;

namespace Meridian.Domain.Ingestion;

/// <summary>
/// Splits one physical line of the Assembly export into fields. The format has no line breaks inside a field, so a line
/// is always a complete record. Supports quoted fields ("a, b") and "" escapes inside them. Anything else involving a
/// quote is malformed and is reported, never repaired: an unbalanced quote, a quote inside an unquoted field, or text
/// after the closing quote of a field.
/// </summary>
public static class CsvLine
{
    /// <returns>True with the fields, or false with the reason the line is malformed.</returns>
    public static bool TrySplit(string line, out List<string> fields, out string? error)
    {
        fields = [];
        error = null;
        var field = new StringBuilder();
        var i = 0;

        while (true)
        {
            field.Clear();
            if (i < line.Length && line[i] == '"')
            {
                var start = i++;
                while (true)
                {
                    if (i >= line.Length)
                    {
                        error = $"a quote opened at position {start + 1} is never closed";
                        return false;
                    }
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }
                        i++; // closing quote
                        break;
                    }
                    field.Append(line[i++]);
                }

                if (i < line.Length && line[i] != ',')
                {
                    error = $"unexpected text after the closing quote at position {i + 1}";
                    return false;
                }
            }
            else
            {
                while (i < line.Length && line[i] != ',')
                {
                    if (line[i] == '"')
                    {
                        error = $"a quote inside an unquoted field at position {i + 1}";
                        return false;
                    }
                    field.Append(line[i++]);
                }
            }

            fields.Add(field.ToString());
            if (i >= line.Length)
                return true;
            i++; // the comma; a trailing comma means one more (empty) field
        }
    }
}
