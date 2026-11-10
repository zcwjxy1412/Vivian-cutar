using System.Text;
using VIvian.Infrastructure.Engines;

namespace VIvian.Infrastructure.Processes;

/// <summary>Serializes real desktop work across VIvian profiles and leaves a durable recovery marker.</summary>
public sealed class DesktopOperationLease : IDisposable
{
    public static string LockPath
    {
        get
        {
            // Packaged Windows hosts can redirect AppData to a per-package cache. Use the common profile root.
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(profile))
                throw new InvalidOperationException("无法确认 Windows 用户目录，拒绝访问桌面互斥文件。");
            return Path.Combine(profile, ".vivian-m6", "desktop-operation.lock");
        }
    }

    private readonly FileStream _file;
    private readonly byte[] _owner;
    private readonly string _lockPath;

    private DesktopOperationLease(FileStream file, byte[] owner, string lockPath, bool recovering)
    {
        _file = file;
        _owner = owner;
        _lockPath = lockPath;
        Recovering = recovering;
    }

    public bool Recovering { get; }

    internal string VerifyCommonOwner(bool requireClean = false)
    {
        if (!string.Equals(_lockPath, LockPath, StringComparison.OrdinalIgnoreCase) || _file.SafeFileHandle.IsClosed)
            throw new InvalidOperationException("未持有共同桌面锁。");
        VerifyHandle(_file, _lockPath);
        if (requireClean && _file.Length != 0)
            throw new InvalidOperationException("桌面锁仍有待恢复标记。");
        return Encoding.UTF8.GetString(_owner);
    }

    public static DesktopOperationLease Acquire(string profileDirectory, bool recovery = false) =>
        AcquireAt(LockPath, profileDirectory, recovery, WindowsProcessSupervisor.IsCurrentProcessElevated());

    internal static DesktopOperationLease? TryAcquireRecovery(string profileDirectory) =>
        Open(LockPath, profileDirectory, recovery: true, requireExisting: true, skipForeignOwner: true);

    internal static DesktopOperationLease AcquireAt(string lockPath, string profileDirectory, bool recovery = false,
        bool requireExisting = false) => Open(lockPath, profileDirectory, recovery, requireExisting, skipForeignOwner: false)!;

    private static DesktopOperationLease? Open(string lockPath, string profileDirectory, bool recovery,
        bool requireExisting, bool skipForeignOwner)
    {
        lockPath = Path.GetFullPath(lockPath);
        if (ReadOnlyFileAccess.FindReparsePoint(lockPath) is not null)
            throw new InvalidOperationException("桌面互斥文件或目录是链接，拒绝访问。");
        if (requireExisting && !File.Exists(lockPath))
            throw new InvalidOperationException("请先在普通权限下运行 Initialize-M6DesktopLock.ps1 初始化桌面互斥文件。");
        var owner = Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory)).ToUpperInvariant());
        FileStream file;
        try
        {
            if (!requireExisting) Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            file = new FileStream(lockPath, requireExisting ? FileMode.Open : FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("另一个窗口正在使用真实桌面，或无法访问桌面互斥文件。", ex);
        }

        try
        {
            VerifyHandle(file, lockPath);
            var pending = file.Length != 0;
            if (pending)
            {
                if (!recovery || file.Length != owner.Length)
                    return ForeignOwner();
                var previousOwner = new byte[owner.Length];
                file.ReadExactly(previousOwner);
                if (!previousOwner.AsSpan().SequenceEqual(owner))
                    return ForeignOwner();
            }
            return new(file, owner, lockPath, pending);

            DesktopOperationLease? ForeignOwner()
            {
                if (!skipForeignOwner) throw PendingCleanup();
                file.Dispose();
                return null;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Call immediately before the first desktop mutation. A crash leaves the marker intact.</summary>
    public void MarkDirty()
    {
        VerifyHandle(_file, _lockPath);
        _file.Position = 0;
        _file.Write(_owner);
        _file.SetLength(_owner.Length);
        _file.Flush(flushToDisk: true);
    }

    /// <summary>Call only after process cleanup and configuration restoration have both been verified.</summary>
    public void ConfirmClean()
    {
        VerifyHandle(_file, _lockPath);
        _file.SetLength(0);
        _file.Flush(flushToDisk: true);
    }

    public void Dispose() => _file.Dispose();

    private static void VerifyHandle(FileStream file, string lockPath)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(lockPath) is not null
            || !ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, lockPath)
            || (OperatingSystem.IsWindows() && ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) != 1))
            throw new InvalidOperationException("桌面互斥文件的实际路径或链接数不安全，拒绝写入。");
    }

    private static InvalidOperationException PendingCleanup() =>
        new("上一次桌面操作的清理或配置恢复待确认。请在原档案核对并完成恢复后再运行。");
}
