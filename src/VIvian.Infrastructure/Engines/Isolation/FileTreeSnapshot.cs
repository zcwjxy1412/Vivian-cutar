using System.Security.Cryptography;

namespace VIvian.Infrastructure.Engines.Isolation;

/// <summary>One entry of a snapshot. Links are recorded as links and never followed or hashed.</summary>
public sealed record SnapshotEntry(string RelativePath, bool IsDirectory, bool IsLink, long Length, DateTime LastWriteUtc, string? Sha256);

public sealed record FileTreeSnapshot(string Root, IReadOnlyDictionary<string, SnapshotEntry> Entries, bool Truncated)
{
    /// <summary>Records every file and folder under <paramref name="root"/> (bounded). Reads only.</summary>
    public static async Task<FileTreeSnapshot> CaptureAsync(string root, int maxEntries = 200_000, CancellationToken ct = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var entries = new Dictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        var truncated = false;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(full));
        while (pending.Count > 0 && !truncated)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= maxEntries) { truncated = true; break; }
                var relative = Path.GetRelativePath(full, entry.FullName).Replace('\\', '/');
                var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                if (entry is DirectoryInfo sub)
                {
                    entries[relative] = new(relative, true, isLink, 0, sub.LastWriteTimeUtc, null);
                    if (!isLink) pending.Push(sub);
                    continue;
                }
                var file = (FileInfo)entry;
                string? hash = null;
                if (!isLink)
                {
                    try
                    {
                        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
                        hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hash = null; }
                }
                entries[relative] = new(relative, false, isLink, isLink ? 0 : file.Length, file.LastWriteTimeUtc, hash);
            }
        }
        return new(full, entries, truncated);
    }

    public FileTreeDiff Compare(FileTreeSnapshot after)
    {
        var added = after.Entries.Keys.Where(k => !Entries.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var removed = Entries.Keys.Where(k => !after.Entries.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var modified = Entries.Where(kv => after.Entries.TryGetValue(kv.Key, out var now) && Changed(kv.Value, now))
            .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        return new(added, removed, modified, Truncated || after.Truncated);
    }

    private static bool Changed(SnapshotEntry before, SnapshotEntry after) =>
        before.IsDirectory != after.IsDirectory || before.IsLink != after.IsLink
        || (!before.IsDirectory && (before.Length != after.Length || before.Sha256 != after.Sha256
            || before.Sha256 is null && before.LastWriteUtc != after.LastWriteUtc));
}

/// <summary>Before/after difference. Files created and deleted again between snapshots cannot be seen.</summary>
public sealed record FileTreeDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Modified, bool Incomplete)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Modified.Count == 0;
}
