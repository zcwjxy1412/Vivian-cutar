using System.Security.Cryptography;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.Isolation;

public enum IsolationState { Ready, Rejected, Failed }

public sealed record IsolatedFile(string RelativePath, long Length, string Sha256);

/// <summary>
/// A private byte-for-byte copy of an engine installation, used so diagnostic commands can never touch the user's
/// real installation or configuration. <see cref="Root"/> is only set when <see cref="State"/> is Ready.
/// </summary>
public sealed record IsolatedCopy(IsolationState State, string? Root, string? Container, IReadOnlyList<IsolatedFile> Files,
    long TotalBytes, EngineErrorCategory Category, string Detail);

public sealed record IsolationLimits(int MaxFiles, long MaxBytes, TimeSpan Timeout)
{
    public static IsolationLimits Default { get; } = new(200_000, 8L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(20));
}

/// <summary>
/// Prepares and discards isolated copies. Refuses sources that contain links (symbolic links, junctions, mount points)
/// or hard-linked files, refuses destinations inside the source (or the reverse), and verifies every copied file is a
/// new, independent file. Nothing is executed. A failed or rejected copy is removed again.
/// </summary>
public static class IsolatedCopyBuilder
{
    /// <summary>Marker beside (not inside) the copied installation; only folders carrying it are ever deleted.</summary>
    public const string MarkerName = ".vivian-isolated";
    private const string InstallFolder = "install";

    public static Task<IsolatedCopy> PrepareAsync(string sourceRoot, string workspace, CancellationToken ct = default) =>
        PrepareAsync(sourceRoot, workspace, IsolationLimits.Default, ct);

    public static async Task<IsolatedCopy> PrepareAsync(string sourceRoot, string workspace, IsolationLimits limits, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Path.IsPathFullyQualified(sourceRoot)
            || string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace))
            return Reject(EngineErrorCategory.PathInvalid, "源目录与隔离工作区都必须是完整的本机路径。");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (IsSameOrUnder(parent, source) || IsSameOrUnder(source, parent))
            return Reject(EngineErrorCategory.UnsafeLayout, "隔离工作区不能位于安装目录内，安装目录也不能位于工作区内。");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(limits.Timeout);
        string? container = null;
        try
        {
            var (exists, sourceLink) = await ReadOnlyFileAccess.BoundedAsync(
                () => (Directory.Exists(source), Directory.Exists(source) ? ReadOnlyFileAccess.FindReparsePoint(source) : null),
                ReadOnlyFileAccess.QueryTimeout, limit.Token).ConfigureAwait(false);
            if (!exists) return Reject(EngineErrorCategory.NotInstalled, "源安装目录不存在。");
            if (sourceLink is not null) return Reject(EngineErrorCategory.LinkRejected, "源安装路径中含有链接，未复制。");

            Directory.CreateDirectory(parent);
            if (ReadOnlyFileAccess.FindReparsePoint(parent) is not null)
                return Reject(EngineErrorCategory.LinkRejected, "隔离工作区路径中含有链接，未复制。");
            container = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(container);
            await File.WriteAllTextAsync(Path.Combine(container, MarkerName), "VIvian isolated diagnostic copy", limit.Token).ConfigureAwait(false);
            var root = Path.Combine(container, InstallFolder);
            Directory.CreateDirectory(root);

            var files = new List<IsolatedFile>();
            long total = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(source));
            while (pending.Count > 0)
            {
                limit.Token.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    limit.Token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, entry.FullName);
                    var target = Path.GetFullPath(Path.Combine(root, relative));
                    if (!IsSameOrUnder(target, root) || target == Path.TrimEndingDirectorySeparator(root))
                        return Discard(container, EngineErrorCategory.UnsafeLayout, $"条目“{relative}”会落在隔离副本之外，已拒绝。");
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        return Discard(container, EngineErrorCategory.LinkRejected, $"安装目录中的“{relative}”是链接（符号链接、目录联接或挂载点），已拒绝复制。");
                    if (entry is DirectoryInfo sub)
                    {
                        Directory.CreateDirectory(target);
                        pending.Push(sub);
                        continue;
                    }
                    if (files.Count >= limits.MaxFiles)
                        return Discard(container, EngineErrorCategory.UnsafeLayout, $"文件数超过上限 {limits.MaxFiles}，已停止复制。");
                    var copied = await CopyFileAsync(entry.FullName, target, relative, limits.MaxBytes - total, limit.Token).ConfigureAwait(false);
                    if (copied.Error is { } error) return Discard(container, error.Category, error.Detail);
                    files.Add(copied.File!);
                    total += copied.File!.Length;
                }
            }
            files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
            return new(IsolationState.Ready, root, container, files, total, EngineErrorCategory.None,
                $"已在隔离工作区复制 {files.Count} 个文件（{total:N0} 字节）；原安装目录未被修改。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Discard(container, EngineErrorCategory.Timeout, "复制超时，已删除不完整的副本。");
        }
        catch (OperationCanceledException)
        {
            Discard(container, EngineErrorCategory.Internal, "");
            throw;
        }
        catch (TimeoutException)
        {
            return Discard(container, EngineErrorCategory.Timeout, "访问安装目录超时，没有复制。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Discard(container, EngineErrorCategory.AccessDenied, $"复制失败（{ex.GetType().Name}），已删除不完整的副本。", IsolationState.Failed);
        }
    }

    private sealed record CopyError(EngineErrorCategory Category, string Detail);
    private sealed record CopyOutcome(IsolatedFile? File, CopyError? Error);

    private static async Task<CopyOutcome> CopyFileAsync(string sourcePath, string targetPath, string relative, long remaining, CancellationToken ct)
    {
        await using var input = ReadOnlyFileAccess.OpenForStableRead(sourcePath);
        if (!ReadOnlyFileAccess.HandlePathMatches(input.SafeFileHandle, sourcePath))
            return new(null, new(EngineErrorCategory.LinkRejected, $"“{relative}”打开后的实际路径与预期不一致，已拒绝。"));
        if (ReadOnlyFileAccess.LinkCount(input.SafeFileHandle) is > 1)
            return new(null, new(EngineErrorCategory.UnsafeLayout, $"“{relative}”有多个硬链接，可能与其他位置的文件共享内容，已拒绝复制。"));
        if (input.Length > remaining)
            return new(null, new(EngineErrorCategory.UnsafeLayout, "总大小超过隔离副本上限，已停止复制。"));
        var sourceIdentity = ReadOnlyFileAccess.Identity(input.SafeFileHandle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                length += read;
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            // The copy must be a new, independent file: one link, and not the source file itself.
            if (ReadOnlyFileAccess.LinkCount(output.SafeFileHandle) is > 1
                || (sourceIdentity is not null && Equals(ReadOnlyFileAccess.Identity(output.SafeFileHandle), sourceIdentity)))
                return new(null, new(EngineErrorCategory.UnsafeLayout, $"“{relative}”的副本不是独立文件，已拒绝。"));
        }
        File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath));
        return new(new(relative.Replace('\\', '/'), length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()), null);
    }

    /// <summary>Deletes an isolated copy created by this class. Folders without the marker are never touched.</summary>
    public static bool Delete(IsolatedCopy copy)
    {
        if (copy.Container is null) return false;
        return DeleteContainer(copy.Container);
    }

    private static bool DeleteContainer(string container)
    {
        try
        {
            if (!File.Exists(Path.Combine(container, MarkerName)) || ReadOnlyFileAccess.FindReparsePoint(container) is not null) return false;
            // Directory.Delete does not follow links inside; the copy contains none (they were refused).
            Directory.Delete(container, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsSameOrUnder(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.Equals(r, comparison) || p.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }

    private static IsolatedCopy Reject(EngineErrorCategory category, string detail) =>
        new(IsolationState.Rejected, null, null, [], 0, category, detail);

    private static IsolatedCopy Discard(string? container, EngineErrorCategory category, string detail, IsolationState state = IsolationState.Rejected)
    {
        if (container is not null) DeleteContainer(container);
        return new(state, null, null, [], 0, category, detail);
    }
}
