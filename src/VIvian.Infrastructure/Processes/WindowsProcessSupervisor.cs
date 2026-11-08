using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using VIvian.Core.Execution;
using static VIvian.Infrastructure.Processes.NativeMethods;

namespace VIvian.Infrastructure.Processes;

/// <summary>
/// Starts each engine inside its own Windows Job Object. The root process is created suspended and placed in the
/// job before it runs any code, so every descendant it creates is inside the job too. Only processes in this job
/// are ever terminated; nothing is looked up or stopped by name. The job is marked kill-on-close: if VIvian exits
/// or crashes, Windows terminates the engine processes it owned.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessSupervisor : IProcessSupervisor
{
    /// <summary>
    /// Opens an engine's own window for the user (e.g. March7th Launcher.exe), exactly as a double-click would:
    /// through the shell, so Windows shows its UAC prompt when the program asks for administrator rights and the user
    /// decides. The program is not placed in a Job, is never controlled and is never ended by VIvian. Only an
    /// existing .exe directly inside <paramref name="installDirectory"/> can be opened.
    /// </summary>
    public static void OpenUserInterface(string installDirectory, string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只能打开安装目录里的程序。", nameof(fileName));
        var exe = Path.Combine(Path.GetFullPath(installDirectory), fileName);
        if (!File.Exists(exe)) throw new FileNotFoundException("安装目录里没有找到 " + fileName + "。", exe);
        using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
        { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Starts a user-facing game through the shell and returns its process, if the shell exposes one.</summary>
    public static System.Diagnostics.Process? StartUserProcess(string fileName, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(fileName) || !File.Exists(fileName)) return null;
        return System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        });
    }

    /// <summary>
    /// Starts the headless input host with the UAC prompt. The host is VIvian's own executable; it is not placed in
    /// the engine job because it must outlive a single engine run and only performs validated window input.
    /// </summary>
    public static System.Diagnostics.Process StartElevatedProcess(string fileName, string arguments, string workingDirectory)
    {
        var info = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory,
        };
        return System.Diagnostics.Process.Start(info)
            ?? throw new System.ComponentModel.Win32Exception("StartProcess returned no process.");
    }

    /// <summary>True when VIvian itself runs with an elevated (administrator) token.</summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Starts VIvian's own input host as a direct child of an already elevated VIvian: it inherits the token the user
    /// approved when VIvian was opened, so no further UAC prompt is involved.
    /// </summary>
    public static System.Diagnostics.Process StartInheritedProcess(string fileName, string arguments, string workingDirectory) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        }) ?? throw new System.ComponentModel.Win32Exception("StartProcess returned no process.");

    /// <summary>Terminates only the exact process handle VIvian started, never a process found by name.</summary>
    public static bool TerminateOwnedProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (process.HasExited) return true;
            TerminateProcess(process.SafeHandle, 1);
            return process.WaitForExit(5000);
        }
        catch { return false; }
    }

    /// <summary>
    /// Terminates a Star Rail process after checking both its image name and executable file name. Used by the
    /// elevated input host, which can reach an already-running game that the medium-integrity VIvian cannot.
    /// </summary>
    public static bool TerminateStarRail(int processId, out string detail) =>
        TerminateStarRail(processId, 0, out detail);

    /// <summary>
    /// As above, and when <paramref name="expectedStartUtcTicks"/> is known the process start time must match it,
    /// so a recycled PID belonging to a later process is refused rather than terminated.
    /// </summary>
    public static bool TerminateStarRail(int processId, long expectedStartUtcTicks, out string detail)
    {
        detail = "";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, "StarRail", StringComparison.OrdinalIgnoreCase))
            {
                detail = "拒绝结束非星铁进程。";
                return false;
            }
            if (expectedStartUtcTicks != 0)
            {
                long actual;
                try { actual = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception) { detail = "无法读取星铁进程启动时间，无法证明进程身份，拒绝结束。"; return false; }
                if (actual != expectedStartUtcTicks)
                {
                    detail = "进程号已被另一个星铁进程复用（启动时间不一致），拒绝结束。";
                    return false;
                }
            }
            // Process.MainModule can itself fail with access denied; open an explicit handle with
            // PROCESS_TERMINATE through the elevated host instead of using the limited query handle.
            using var handle = NativeMethods.OpenProcess(0x0001, false, processId);
            if (handle.IsInvalid)
            {
                detail = "无法打开星铁进程（Win32 " + Marshal.GetLastWin32Error() + "）。";
                return false;
            }
            if (!NativeMethods.TerminateProcess(handle, 1))
            {
                detail = "无法结束星铁进程（Win32 " + Marshal.GetLastWin32Error() + "）。";
                return false;
            }
            if (!process.WaitForExit(8000))
            {
                detail = "星铁进程在 8 秒内未退出。";
                return false;
            }
            detail = "killed";
            return true;
        }
        catch (ArgumentException) { detail = "already-exited"; return true; }
        catch (Exception ex) { detail = "无法结束星铁进程：" + ex.Message; return false; }
    }

    // Keep our launches serialized while inheritable pipe ends exist in this process.
    private static readonly Lock LaunchLock = new();

    public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(spec.FileName) || !File.Exists(spec.FileName))
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "引擎程序不存在或路径不是绝对路径。");
        if (!Directory.Exists(spec.WorkingDirectory))
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "工作目录不存在。");
        var commandLine = (CommandLine.Build(spec.FileName, spec.Arguments) + '\0').ToCharArray();
        var environment = spec.Environment is { Count: > 0 } ? CommandLine.BuildEnvironmentBlock(spec.Environment) : null;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        SafeFileHandle? stdoutRead = null, stdoutWrite = null, stdinRead = null, stdinWrite = null;
        SafeProcessHandle? process = null;
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");

            var inherit = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref inherit, 0) || !CreatePipe(out stdinRead, out stdinWrite, ref inherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
            // Only the child's ends are inheritable.
            if (!SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0) ||
                !SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed");

            using var inherited = new InheritedHandleList(stdinRead, stdoutWrite);
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = stdinRead.DangerousGetHandle(),
                    hStdOutput = stdoutWrite.DangerousGetHandle(),
                    hStdError = stdoutWrite.DangerousGetHandle(),
                },
                AttributeList = inherited.Pointer,
            };
            PROCESS_INFORMATION info;
            var envHandle = environment is null ? default : GCHandle.Alloc(environment, GCHandleType.Pinned);
            try
            {
                var flags = CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT |
                    CREATE_NEW_PROCESS_GROUP | EXTENDED_STARTUPINFO_PRESENT;
                bool created;
                lock (LaunchLock)
                {
                    created = CreateProcessExtended(spec.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags,
                        environment is null ? IntPtr.Zero : envHandle.AddrOfPinnedObject(), spec.WorkingDirectory,
                        ref startup, out info);
                }
                if (!created)
                    throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true,
                        $"无法启动引擎进程（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }
            finally
            {
                if (envHandle.IsAllocated) envHandle.Free();
            }

            process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            if (!AssignProcessToJobObject(job, process))
            {
                var error = Marshal.GetLastWin32Error();
                // The process never ran: it is still suspended. Terminate this exact process handle and confirm.
                TerminateProcess(process, 1);
                CloseHandle(info.hThread);
                var gone = WaitForExit(process, TimeSpan.FromSeconds(5));
                throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: gone,
                    $"无法把引擎进程放入作业对象（Win32 错误 {error}）。");
            }
            ResumeThread(info.hThread);
            CloseHandle(info.hThread);
            // Close the child's pipe ends in our process, otherwise end-of-output is never observed.
            stdoutWrite.Dispose();
            stdinRead.Dispose();

            var group = new SupervisedProcessGroup(runId, info.dwProcessId, job, process, stdoutRead, stdinWrite);
            return Task.FromResult<ISupervisedProcessGroup>(group);
        }
        catch
        {
            // Closing the job (kill-on-close) terminates anything that might have been placed in it.
            job.Dispose();
            process?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            throw;
        }
    }

    internal static bool WaitForExit(SafeProcessHandle process, TimeSpan timeout)
    {
        using var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false) };
        return wait.WaitOne(timeout);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SupervisedProcessGroup : ISupervisedProcessGroup
{
    private readonly SafeFileHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly FileStream _stdin;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _exitRegistration;
    private readonly ManualResetEvent _exitWait;
    private readonly SemaphoreSlim _stdinGate = new(1, 1);
    private int _disposed;

    public SupervisedProcessGroup(Guid runId, int processId, SafeFileHandle job, SafeProcessHandle process,
        SafeFileHandle stdoutRead, SafeFileHandle stdinWrite)
    {
        RunId = runId;
        RootProcessId = processId;
        _job = job;
        _process = process;
        RootStartTime = GetProcessTimes(process, out var creation, out _, out _, out _)
            ? DateTimeOffset.FromFileTime(creation.ToLong()) : DateTimeOffset.UtcNow;
        _stdin = new FileStream(stdinWrite, FileAccess.Write, 1, isAsync: false);
        var stdout = new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false);
        // Synchronous pipe reads run on a dedicated thread so they never occupy the thread pool.
        new Thread(() => ReadOutput(stdout)) { IsBackground = true, Name = "VIvian engine output" }.Start();
        _exitWait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false) };
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitWait, (_, _) =>
        {
            try { _exit.TrySetResult(NativeMethods.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1); }
            catch (ObjectDisposedException) { _exit.TrySetResult(-1); }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public Guid RunId { get; }
    public int RootProcessId { get; }
    public DateTimeOffset RootStartTime { get; }
    public IAsyncEnumerable<string> OutputLines => _lines.Reader.ReadAllAsync();

    public int ActiveProcessCount
    {
        get
        {
            if (Volatile.Read(ref _disposed) == 1) return 0;
            return QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, out var info,
                Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
                ? (int)info.ActiveProcesses
                : throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject failed");
        }
    }

    public bool OwnsProcess(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (Volatile.Read(ref _disposed) == 1) return false;
        return IsProcessInJob(process, _job, out var inJob)
            ? inJob
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "IsProcessInJob failed");
    }

    public Task<int> WaitForRootExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public async Task<bool> SendInputLineAsync(string line, CancellationToken ct)
    {
        await _stdinGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await Task.Run(() => { _stdin.Write(bytes); _stdin.Flush(); }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false; // the engine closed its input or already exited
        }
        finally
        {
            _stdinGate.Release();
        }
    }

    public async Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (ActiveProcessCount == 0) return CleanupState.Confirmed;
        TerminateJobObject(_job, 1);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ActiveProcessCount == 0) return CleanupState.Confirmed;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return ActiveProcessCount == 0 ? CleanupState.Confirmed : CleanupState.Unconfirmed;
    }

    private void ReadOutput(FileStream stdout)
    {
        try
        {
            using var reader = new StreamReader(stdout, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            while (reader.ReadLine() is { } line) _lines.Writer.TryWrite(line);
        }
        catch (IOException)
        {
            // Pipe broken: every writer is gone.
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        _exitRegistration.Unregister(null);
        _exitWait.Dispose();
        try { _stdin.Dispose(); } catch (IOException) { /* engine already gone */ }
        // Kill-on-close: anything still in the job is terminated when the last job handle closes.
        _job.Dispose();
        _process.Dispose();
        _stdinGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
