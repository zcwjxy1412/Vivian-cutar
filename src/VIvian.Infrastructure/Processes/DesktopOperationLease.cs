using System.Text;

namespace VIvian.Infrastructure.Processes;

/// <summary>Serializes real desktop work across VIvian profiles and leaves a durable recovery marker.</summary>
public sealed class DesktopOperationLease : IDisposable
{
    public static string LockPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIvian-M6", "desktop-operation.lock");

    private readonly FileStream _file;
    private readonly byte[] _owner;

    private DesktopOperationLease(FileStream file, byte[] owner, bool recovering)
    {
        _file = file;
        _owner = owner;
        Recovering = recovering;
    }

    public bool Recovering { get; }

    public static DesktopOperationLease Acquire(string profileDirectory, bool recovery = false) =>
        AcquireAt(LockPath, profileDirectory, recovery);

    internal static DesktopOperationLease AcquireAt(string lockPath, string profileDirectory, bool recovery = false)
    {
        var owner = Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory)).ToUpperInvariant());
        FileStream file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("另一个窗口正在使用真实桌面，或无法访问桌面互斥文件。", ex);
        }

        try
        {
            var pending = file.Length != 0;
            if (pending)
            {
                if (!recovery || file.Length != owner.Length)
                    throw PendingCleanup();
                var previousOwner = new byte[owner.Length];
                file.ReadExactly(previousOwner);
                if (!previousOwner.AsSpan().SequenceEqual(owner))
                    throw PendingCleanup();
            }
            return new(file, owner, pending);
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
        _file.Position = 0;
        _file.Write(_owner);
        _file.SetLength(_owner.Length);
        _file.Flush(flushToDisk: true);
    }

    /// <summary>Call only after process cleanup and configuration restoration have both been verified.</summary>
    public void ConfirmClean()
    {
        _file.SetLength(0);
        _file.Flush(flushToDisk: true);
    }

    public void Dispose() => _file.Dispose();

    private static InvalidOperationException PendingCleanup() =>
        new("上一次桌面操作的清理或配置恢复待确认。请在原档案核对并完成恢复后再运行。");
}
