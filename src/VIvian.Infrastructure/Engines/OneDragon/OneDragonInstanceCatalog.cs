using System.Globalization;
using System.Text;

namespace VIvian.Infrastructure.Engines.OneDragon;

public enum OneDragonInstanceRunMode { All, CurrentOnly }

public enum OneDragonInstanceStatus { Available, Missing, Empty, Invalid, Unavailable, LinkRejected }

// Index is the upstream script profile ID. These fields cannot identify a logged-in game account.
public sealed record OneDragonInstance(int Index, bool Active, bool IncludedByDefault, bool ForceLogin);

public sealed record OneDragonInstanceList(OneDragonInstanceStatus Status, IReadOnlyList<OneDragonInstance> Instances)
{
    public OneDragonInstanceRunMode RunMode { get; init; }

    public bool ContainsExactlyOne(IReadOnlyList<int> requested)
    {
        if (Status != OneDragonInstanceStatus.Available || requested.Count != 1) return false;
        var target = Instances.FirstOrDefault(instance => instance.Index == requested[0]);
        // The launcher echoes -i even in current-only mode, where the runtime uses its active profile instead.
        return target is not null && (RunMode == OneDragonInstanceRunMode.All
            || (target.Active && Instances.Count(instance => instance.Active) == 1));
    }
}

/// <summary>Reads the persisted script profiles in OneDragon v2.6.0 without opening their account files.</summary>
public static class OneDragonInstanceCatalog
{
    public static async Task<OneDragonInstanceList> ReadAsync(string installRoot, CancellationToken ct = default)
    {
        try
        {
            var text = await OneDragonConfigRead.ReadAsync(installRoot, "one_dragon.yml", ct).ConfigureAwait(false);
            return text is null ? new(OneDragonInstanceStatus.Missing, []) : Parse(text);
        }
        catch (OneDragonConfigLinkException) { return new(OneDragonInstanceStatus.LinkRejected, []); }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        { return new(OneDragonInstanceStatus.Invalid, []); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        { return new(OneDragonInstanceStatus.Unavailable, []); }
    }

    internal static OneDragonInstanceList Parse(string yaml)
    {
        var parsed = OneDragonConfigRead.ReadSequence(yaml, "instance_list");
        if (parsed is null) return new(OneDragonInstanceStatus.Invalid, []);
        var runMode = parsed.Scalars.GetValueOrDefault("instance_run")?.Value;
        if (runMode is not (null or "全部实例" or "仅运行当前"))
            return new(OneDragonInstanceStatus.Invalid, []);

        var instances = new List<OneDragonInstance>();
        var ids = new HashSet<int>();
        foreach (var fields in parsed.Rows)
        {
            var instance = ParseInstance(fields);
            if (instance is null || !ids.Add(instance.Index)) return new(OneDragonInstanceStatus.Invalid, []);
            instances.Add(instance);
        }
        if (instances.Count == 0) return new(OneDragonInstanceStatus.Empty, []);
        return new(OneDragonInstanceStatus.Available, instances.AsReadOnly())
        {
            RunMode = runMode == "仅运行当前" ? OneDragonInstanceRunMode.CurrentOnly : OneDragonInstanceRunMode.All,
        };
    }

    private static OneDragonInstance? ParseInstance(Dictionary<string, ScalarLocation> fields)
    {
        if (fields.Keys.Any(key => key is not ("idx" or "name" or "active" or "active_in_od" or "force_login_before_run")))
            return null;
        var index = fields.GetValueOrDefault("idx");
        var force = fields.GetValueOrDefault("force_login_before_run");
        var forceLogin = OneDragonConfigRead.ReadBool(force);
        if (index is not { Quote: null } || index.Value.Length is < 1 or > 2
            || !index.Value.All(char.IsAsciiDigit) || index.Value[0] == '0'
            || !fields.ContainsKey("name")
            || OneDragonConfigRead.ReadBool(fields.GetValueOrDefault("active")) is not { } active
            || OneDragonConfigRead.ReadBool(fields.GetValueOrDefault("active_in_od")) is not { } included
            || (force is not null && forceLogin is null))
            return null;

        return new(int.Parse(index.Value, CultureInfo.InvariantCulture), active, included, forceLogin ?? false);
    }
}

internal sealed class OneDragonConfigLinkException : IOException;

internal static class OneDragonConfigRead
{
    private const int MaxConfigBytes = 64 * 1024;

    internal static Task<string?> ReadAsync(string root, string file, CancellationToken ct) =>
        ReadOnlyFileAccess.BoundedAsync(() => Read(Path.Combine(root, "config", file)), ReadOnlyFileAccess.QueryTimeout, ct);

    private static string? Read(string path)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null) throw new OneDragonConfigLinkException();
        try
        {
            using var stream = ReadOnlyFileAccess.OpenForStableRead(path);
            if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, path))
                throw new OneDragonConfigLinkException();
            if (stream.Length > MaxConfigBytes) throw new InvalidDataException();
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    internal static List<string> Lines(string yaml) =>
        YamlScalarEditor.Lines(yaml).Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#')).ToList();

    internal sealed record ParsedSequence(bool HasSequence, Dictionary<string, ScalarLocation> Scalars,
        List<Dictionary<string, ScalarLocation>> Rows);

    // v2.6.0 uses PyYAML's block mapping with an indentless sequence and two-space continuation keys.
    internal static ParsedSequence? ReadSequence(string yaml, string sequenceKey)
    {
        var lines = Lines(yaml);
        var scalars = new Dictionary<string, ScalarLocation>(StringComparer.Ordinal);
        var rows = new List<Dictionary<string, ScalarLocation>>();
        var hasSequence = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var key = KeyOf(lines[i]);
            if (key != sequenceKey)
            {
                if (!TryAddScalar(scalars, lines[i], key)) return null;
                continue;
            }

            if (hasSequence) return null;
            hasSequence = true;
            var tail = lines[i][(key.Length + 1)..].Trim();
            if (tail == "[]") continue;
            if (tail.Length != 0 && !tail.StartsWith('#')) return null;
            if (i + 1 == lines.Count || !lines[i + 1].StartsWith("- ", StringComparison.Ordinal))
                return null;
            while (i + 1 < lines.Count && lines[i + 1].StartsWith("- ", StringComparison.Ordinal))
            {
                var fields = new Dictionary<string, ScalarLocation>(StringComparer.Ordinal);
                var first = lines[++i][2..];
                if (!TryAddScalar(fields, first, KeyOf(first))) return null;
                while (i + 1 < lines.Count && lines[i + 1].StartsWith("  ", StringComparison.Ordinal))
                {
                    var next = lines[++i][2..];
                    if (!TryAddScalar(fields, next, KeyOf(next))) return null;
                }
                rows.Add(fields);
            }
        }
        return new(hasSequence, scalars, rows);
    }

    private static bool TryAddScalar(Dictionary<string, ScalarLocation> fields, string line, string? key) =>
        key is not null && YamlScalarEditor.Locate([line], key) is { } scalar && fields.TryAdd(key, scalar);

    internal static bool? ReadBool(ScalarLocation? field) => field switch
    {
        { Quote: null, Value: "true" } => true,
        { Quote: null, Value: "false" } => false,
        _ => null,
    };

    internal static string? KeyOf(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0 || (colon + 1 < line.Length && line[colon + 1] != ' ')) return null;
        var key = line[..colon];
        return key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? key : null;
    }
}
