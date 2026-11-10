using System.Security.Cryptography;
using System.Text;

namespace VIvian.Infrastructure;

public enum InstanceLockResult
{
    /// <summary>This process is the only VIvian instance for the data directory.</summary>
    Acquired,
    /// <summary>The previous owner exited without releasing the lock (e.g. a crash). Acquired; reconcile before running.</summary>
    AcquiredAfterAbandoned,
    /// <summary>Another instance holds the lock.</summary>
    AlreadyRunning,
    /// <summary>The lock exists but cannot be opened (another user, session or elevation level). Its state is unknown,
    /// so this is treated like <see cref="AlreadyRunning"/> — never as "nobody is running".</summary>
    AccessDenied,
}

/// <summary>
/// One VIvian instance per data directory. The named mutex is held by a dedicated thread, because mutex ownership
/// belongs to a thread and async code may resume elsewhere. This only coordinates VIvian instances; it does not
/// and cannot stop a user from starting an upstream engine by hand.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly ManualResetEventSlim _release;
    private readonly Thread? _owner;
    private readonly string _name;
    private int _disposed;

    private SingleInstanceGuard(InstanceLockResult result, ManualResetEventSlim release, Thread? owner, string name)
    {
        Result = result;
        _release = release;
        _owner = owner;
        _name = name;
    }

    public InstanceLockResult Result { get; }
    public bool IsOwner => Result is InstanceLockResult.Acquired or InstanceLockResult.AcquiredAfterAbandoned;
    internal bool Owns(string directory) => IsOwner && Volatile.Read(ref _disposed) == 0 && _name == NameFor(directory);

    /// <summary>Mutex name derived from the full data directory path (case-insensitive, as on Windows).</summary>
    public static string NameFor(string dataDirectory)
    {
        var full = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24];
        return @"Local\VIvian-" + hash;
    }

    /// <param name="openMutex">Test seam; production uses <see cref="Mutex(bool, string)"/>.</param>
    public static SingleInstanceGuard Acquire(string dataDirectory, Func<string, Mutex>? openMutex = null)
    {
        var name = NameFor(dataDirectory);
        openMutex ??= n => new Mutex(false, n);
        var release = new ManualResetEventSlim(false);
        var decided = new TaskCompletionSource<InstanceLockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() => Hold(name, openMutex, decided, release))
        { IsBackground = true, Name = "VIvian single-instance lock" };
        owner.Start();
        var result = decided.Task.GetAwaiter().GetResult();
        var holding = result is InstanceLockResult.Acquired or InstanceLockResult.AcquiredAfterAbandoned;
        if (!holding) owner.Join();
        return new SingleInstanceGuard(result, release, holding ? owner : null, name);
    }

    private static void Hold(string name, Func<string, Mutex> openMutex,
        TaskCompletionSource<InstanceLockResult> decided, ManualResetEventSlim release)
    {
        Mutex mutex;
        try
        {
            mutex = openMutex(name);
        }
        catch (UnauthorizedAccessException)
        {
            decided.SetResult(InstanceLockResult.AccessDenied);
            return;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            decided.SetResult(InstanceLockResult.AccessDenied);
            return;
        }
        using (mutex)
        {
            InstanceLockResult result;
            try
            {
                result = mutex.WaitOne(0) ? InstanceLockResult.Acquired : InstanceLockResult.AlreadyRunning;
            }
            catch (AbandonedMutexException)
            {
                // The wait succeeded: we own it now, but the previous owner died while holding it.
                result = InstanceLockResult.AcquiredAfterAbandoned;
            }
            catch (UnauthorizedAccessException)
            {
                result = InstanceLockResult.AccessDenied;
            }
            decided.SetResult(result);
            if (result is not (InstanceLockResult.Acquired or InstanceLockResult.AcquiredAfterAbandoned)) return;
            release.Wait();
            mutex.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _release.Set();
        _owner?.Join(TimeSpan.FromSeconds(5));
        _release.Dispose();
    }
}
