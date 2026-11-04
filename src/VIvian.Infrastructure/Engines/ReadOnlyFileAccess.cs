using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace VIvian.Infrastructure.Engines;

/// <summary>
/// Read-only access to files the user selected (engine installations, downloaded release archives). Nothing here
/// writes, executes, follows links, or blocks the caller without a time limit.
/// </summary>
public static class ReadOnlyFileAccess
{
    /// <summary>Limit for a single metadata query (existence, attributes). A slow or offline network path ends here.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs a file-system query off the caller's thread with a time limit. A query that does not return in time is
    /// reported as <see cref="TimeoutException"/>; it is left to finish on its own (it only reads metadata).
    /// </summary>
    public static async Task<T> BoundedAsync<T>(Func<T> query, TimeSpan timeout, CancellationToken ct)
    {
        var task = Task.Run(query, CancellationToken.None);
        try
        {
            return await task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!task.IsCompleted)
        {
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// Returns the first path component — from <paramref name="path"/> itself up to the volume root — that is a
    /// reparse point (symbolic link, junction, mount point), or null. A link anywhere in the chain means the file
    /// that would be read is not necessarily the one under the folder the user chose.
    /// </summary>
    public static string? FindReparsePoint(string path)
    {
        var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break; // volume root: never itself treated as a link
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) return current;
            current = parent;
        }
        return null;
    }

    /// <summary>Opens a file for reading while denying writers, so its content cannot change during the read.</summary>
    public static FileStream OpenForStableRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Volume serial number and file index of an open file (Windows), or null where unavailable. Two handles with the
    /// same identity refer to the same file, whatever path was used to open them.
    /// </summary>
    public static (uint Volume, ulong Index)? Identity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Native.GetFileInformationByHandle(handle, out var info)
            ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : null;
    }

    /// <summary>
    /// True when the open handle's final path (after the system resolved every link) is the path that was asked for.
    /// Always true where the check is unavailable; the reparse-point walk then remains the guard.
    /// </summary>
    public static bool HandlePathMatches(SafeFileHandle handle, string expectedFullPath)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var final = Native.FinalPath(handle);
        if (final is null) return false;
        static string Normalize(string p) => (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + p[8..]
            : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p).TrimEnd('\\');
        return string.Equals(Normalize(final), Normalize(Path.GetFullPath(expectedFullPath)), StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public long CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, char[] buffer, uint size, uint flags);

        public static string? FinalPath(SafeFileHandle handle)
        {
            var buffer = new char[1024];
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length > buffer.Length)
            {
                buffer = new char[length];
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0 || length > buffer.Length) return null;
            }
            return new string(buffer, 0, (int)length);
        }
    }
}
