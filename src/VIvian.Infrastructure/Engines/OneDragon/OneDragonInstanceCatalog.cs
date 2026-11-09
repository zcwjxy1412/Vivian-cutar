using System.Globalization;
using System.Text;

namespace VIvian.Infrastructure.Engines.OneDragon;

public enum OneDragonInstanceStatus { Available, Missing, Empty, Invalid, Unavailable, LinkRejected }

// Index is the upstream script profile ID. These fields cannot identify a logged-in game account.
public sealed record OneDragonInstance(int Index, bool Active, bool IncludedByDefault, bool ForceLogin);

public sealed record OneDragonInstanceList(OneDragonInstanceStatus Status, IReadOnlyList<OneDragonInstance> Instances)
{
    public bool ContainsExactlyOne(IReadOnlyList<int> requested) =>
        Status == OneDragonInstanceStatus.Available && requested.Count == 1
        && Instances.Any(instance => instance.Index == requested[0]);
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

    private static OneDragonInstanceList Parse(string yaml)
    {
        // v2.6.0 writes a block mapping with an indentless sequence and two-space continuation keys.
        // Reject unsupported syntax so a damaged config cannot silently select the default profile.
        var lines = OneDragonConfigRead.Lines(yaml);
        var rootKeys = new HashSet<string>(StringComparer.Ordinal);
        var instances = new List<OneDragonInstance>();
        var ids = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var key = OneDragonConfigRead.KeyOf(lines[i]);
            if (key is null || !rootKeys.Add(key)) return new(OneDragonInstanceStatus.Invalid, []);
            if (key != "instance_list")
            {
                if (YamlScalarEditor.Locate([lines[i]], key) is null) return new(OneDragonInstanceStatus.Invalid, []);
                continue;
            }

            var tail = lines[i][(key.Length + 1)..].Trim();
            if (tail == "[]") continue;
            if (tail.Length != 0 && !tail.StartsWith('#')) return new(OneDragonInstanceStatus.Invalid, []);
            if (i + 1 == lines.Count || !lines[i + 1].StartsWith("- ", StringComparison.Ordinal))
                return new(OneDragonInstanceStatus.Invalid, []);
            while (i + 1 < lines.Count && lines[i + 1].StartsWith("- ", StringComparison.Ordinal))
            {
                var fields = new List<string> { lines[++i][2..] };
                while (i + 1 < lines.Count && lines[i + 1].StartsWith("  ", StringComparison.Ordinal))
                    fields.Add(lines[++i][2..]);
                var instance = ParseInstance(fields);
                if (instance is null || !ids.Add(instance.Index)) return new(OneDragonInstanceStatus.Invalid, []);
                instances.Add(instance);
            }
        }

        return instances.Count == 0
            ? new(OneDragonInstanceStatus.Empty, [])
            : new(OneDragonInstanceStatus.Available, instances.AsReadOnly());
    }

    private static OneDragonInstance? ParseInstance(List<string> fields)
    {
        if (fields.Any(line => OneDragonConfigRead.KeyOf(line) is not
                ("idx" or "name" or "active" or "active_in_od" or "force_login_before_run")))
            return null;

        var index = YamlScalarEditor.Locate(fields, "idx");
        var force = YamlScalarEditor.Locate(fields, "force_login_before_run");
        var forceLogin = ReadBool(force);
        if (index is not { Quote: null } || index.Value.Length is < 1 or > 2
            || !index.Value.All(char.IsAsciiDigit) || index.Value[0] == '0'
            || YamlScalarEditor.Locate(fields, "name") is null
            || ReadBool(YamlScalarEditor.Locate(fields, "active")) is not { } active
            || ReadBool(YamlScalarEditor.Locate(fields, "active_in_od")) is not { } included
            || (fields.Any(line => line.StartsWith("force_login_before_run:", StringComparison.Ordinal)) && forceLogin is null))
            return null;

        return new(int.Parse(index.Value, CultureInfo.InvariantCulture), active, included, forceLogin ?? false);
    }

    private static bool? ReadBool(ScalarLocation? field) => field switch
    {
        { Quote: null, Value: "true" } => true,
        { Quote: null, Value: "false" } => false,
        _ => null,
    };
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

    internal static string? KeyOf(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0 || (colon + 1 < line.Length && line[colon + 1] != ' ')) return null;
        var key = line[..colon];
        return key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? key : null;
    }
}
