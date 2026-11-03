using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VIvian.Infrastructure.Logs;

/// <summary>
/// Reads complete lines appended after enumeration starts. A replacement file is read from
/// its beginning; partial lines are discarded when the file disappears, shrinks or rotates.
/// This component does not interpret engine output or infer task completion.
/// </summary>
public sealed class LogFileTailer
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;

    public LogFileTailer(string path, TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
        _interval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // An async iterator starts on the first MoveNextAsync, not when ReadLinesAsync is called.
        // Establish the baseline before the first await so existing content never leaks into a run.
        FileIdentity? identity = null;
        long offset = 0;
        var partial = new List<byte>();
        var first = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream;
            try
            {
                stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
            }
            catch (FileNotFoundException) { stream = null; }
            catch (DirectoryNotFoundException) { stream = null; }

            if (stream is null)
            {
                identity = null;
                offset = 0;
                partial.Clear();
            }
            else
            {
                await using (stream.ConfigureAwait(false))
                {
                    var found = Identify(stream, _path);
                    if (first)
                    {
                        identity = found;
                        offset = stream.Length;
                    }
                    else if (identity != found)
                    {
                        identity = found;
                        offset = 0;
                        partial.Clear();
                    }

                    // A truncation is a new byte stream, including a new UTF-8 BOM.
                    var end = stream.Length;
                    if (end < offset)
                    {
                        offset = 0;
                        partial.Clear();
                    }

                    stream.Position = offset;
                    var buffer = new byte[8192];
                    while (offset < end)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - offset)),
                            cancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        offset += count;
                        for (var i = 0; i < count; i++)
                        {
                            if (buffer[i] != (byte)'\n')
                            {
                                if (partial.Count == 1024 * 1024)
                                    throw new InvalidDataException("Log line exceeds the 1 MiB safety limit.");
                                partial.Add(buffer[i]);
                                continue;
                            }

                            var start = partial.Count >= 3 && partial[0] == 0xEF &&
                                partial[1] == 0xBB && partial[2] == 0xBF ? 3 : 0;
                            var length = partial.Count - start;
                            if (length > 0 && partial[^1] == (byte)'\r') length--;
                            var line = System.Text.Encoding.UTF8.GetString(partial.GetRange(start, length).ToArray());
                            partial.Clear();
                            yield return line;
                        }
                    }
                }
            }

            first = false;
            await Task.Delay(_interval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static FileIdentity Identify(FileStream stream, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new FileIdentity(info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }

        // On non-Windows development hosts this is a best-effort identity. Production is Windows.
        return new FileIdentity(0, (ulong)File.GetCreationTimeUtc(path).Ticks);
    }

    private readonly record struct FileIdentity(uint Volume, ulong Index);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
