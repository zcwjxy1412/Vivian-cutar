namespace VIvian.Infrastructure.Engines;

/// <summary>Where one scalar value sits in the original text, so it can be replaced without touching anything else.</summary>
public sealed record ScalarLocation(int Line, int ValueStart, int ValueLength, string Value, char? Quote);

/// <summary>
/// Conservative line-level editor for the block-style YAML that March7thAssistant v2026.10.31 writes
/// (assets/config/config.example.yaml layout). Supports "key" at column 0 and "parent.child" one map level deep
/// (child indented by exactly two spaces). Anything else — flow collections, anchors, multi-line scalars, escapes,
/// duplicate keys — is reported as unsupported, which keeps the field read-only instead of guessing.
/// </summary>
public static class YamlScalarEditor
{
    public static ScalarLocation? Locate(IReadOnlyList<string> lines, string path)
    {
        var parts = path.Split('.');
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace)) return null;
        if (parts.Length == 1) return Single(Matches(lines, 0, lines.Count, "", parts[0]));
        var parents = Enumerable.Range(0, lines.Count).Where(i => IsMapHeader(lines[i], parts[0])).ToList();
        if (parents.Count != 1) return null;
        var start = parents[0] + 1;
        var end = start;
        while (end < lines.Count && (lines[end].Length == 0 || lines[end].StartsWith(' ') || lines[end].TrimStart().StartsWith('#')))
            end++;
        return Single(Matches(lines, start, end, "  ", parts[1]));
    }

    /// <summary>Returns the text with one value replaced, keeping prefix, quotes, comment and line ending.</summary>
    public static string Replace(string text, ScalarLocation location, string newValue)
    {
        if (!IsWritable(newValue, location.Quote)) throw new ArgumentException("值包含无法安全写入 YAML 的字符。", nameof(newValue));
        var lines = SplitKeepingEndings(text);
        var line = lines[location.Line];
        // For quoted values the span is the text between the quotes, so the original quotes stay in place.
        lines[location.Line] = line[..location.ValueStart] + newValue + line[(location.ValueStart + location.ValueLength)..];
        return string.Concat(lines);
    }

    /// <summary>Plain values must not need quoting; quoted values must not contain their quote or a backslash.</summary>
    public static bool IsWritable(string value, char? quote)
    {
        if (value.Length == 0 || value.Length > 200 || value.Any(char.IsControl)) return false;
        if (quote is { } q) return !value.Contains(q) && !value.Contains('\\');
        if (value != value.Trim() || value.Contains(" #") || value.Contains(": ") || value.EndsWith(':')) return false;
        return !"[]{}&*!|>'\"%@`#,".Contains(value[0]);
    }

    public static List<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private static ScalarLocation? Single(List<ScalarLocation?> found) => found.Count == 1 ? found[0] : null;

    private static bool IsMapHeader(string line, string key)
    {
        if (!line.StartsWith(key + ":", StringComparison.Ordinal)) return false;
        var rest = line[(key.Length + 1)..].Trim();
        return rest.Length == 0 || rest.StartsWith('#');
    }

    /// <summary>All lines in [start,end) declaring <paramref name="key"/> at exactly <paramref name="indent"/>.
    /// A declaration whose value cannot be parsed safely is returned as null, which makes the result ambiguous.</summary>
    private static List<ScalarLocation?> Matches(IReadOnlyList<string> lines, int start, int end, string indent, string key)
    {
        var result = new List<ScalarLocation?>();
        var head = indent + key + ":";
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(head, StringComparison.Ordinal)) continue;
            result.Add(Parse(i, line, head.Length));
        }
        return result;
    }

    private static ScalarLocation? Parse(int index, string line, int afterColon)
    {
        var pos = afterColon;
        if (pos < line.Length && line[pos] != ' ' && line[pos] != '\t') return null; // "key:value" is not a mapping
        while (pos < line.Length && line[pos] is ' ' or '\t') pos++;
        if (pos >= line.Length) return null; // empty value or nested map: not a scalar
        var first = line[pos];
        if (first is '"' or '\'')
        {
            var close = line.IndexOf(first, pos + 1);
            if (close < 0) return null;
            var inner = line[(pos + 1)..close];
            if (inner.Contains('\\')) return null;
            var tail = line[(close + 1)..].Trim();
            if (tail.Length > 0 && !tail.StartsWith('#')) return null;
            return new(index, pos + 1, inner.Length, inner, first);
        }
        if ("[]{}&*!|>%@`#".Contains(first)) return null;
        var hash = line.IndexOf(" #", pos, StringComparison.Ordinal);
        var raw = hash < 0 ? line[pos..] : line[pos..hash];
        var value = raw.TrimEnd();
        if (value.Length == 0 || value.Contains(": ")) return null;
        return new(index, pos, value.Length, value, null);
    }

    private static List<string> SplitKeepingEndings(string text)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            parts.Add(text[start..end]);
            parts.Add(text[end..(i + 1)]);
            start = i + 1;
        }
        parts.Add(text[start..]);
        // Recombine to "line + ending" pairs addressed by logical line index.
        var lines = new List<string>();
        for (var i = 0; i < parts.Count; i += 2) lines.Add(parts[i] + (i + 1 < parts.Count ? parts[i + 1] : ""));
        return lines;
    }
}
