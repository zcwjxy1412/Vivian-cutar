using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VIvian.Infrastructure.Accounts;

public sealed class InputHostUnavailableException(string message) : Exception(message);

/// <summary>
/// Starts one elevated, headless VIvian instance and talks to it over a per-launch named pipe. The UAC prompt
/// appears once per batch, not once per click.
/// </summary>
public sealed class InputHostClient : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Process? _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private InputHostClient(NamedPipeServerStream pipe, Process? process)
    {
        _pipe = pipe;
        _process = process;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    }

    public static Task<InputHostClient> StartAsync(CancellationToken cancellationToken) =>
        StartAsync(null, cancellationToken);

    /// <summary>
    /// Starts the headless host from the current VIvian executable by default. A diagnostic tool may pass an
    /// explicit installed VIvian.exe; the path is still shown by the UAC prompt before elevation.
    /// </summary>
    public static async Task<InputHostClient> StartAsync(string? hostExecutable, CancellationToken cancellationToken)
    {
        var executable = string.IsNullOrWhiteSpace(hostExecutable) ? Environment.ProcessPath : hostExecutable;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InputHostUnavailableException("无法确定 VIvian 可执行文件路径，不能启动管理员输入宿主。");
        var pipeName = InputHostProtocol.PipePrefix + Guid.NewGuid().ToString("N");
        // The pipe server lives in this medium-integrity process. If the elevated host created it,
        // its high mandatory label would deny this process's write-up (UIPI applies to pipes too).
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process;
        try
        {
            process = Processes.WindowsProcessSupervisor.StartElevatedProcess(executable,
                $"{InputHostProtocol.Argument} {pipeName}", AppContext.BaseDirectory);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new InputHostUnavailableException("已取消管理员授权，无法操作以管理员运行的游戏登录界面。");
        }
        catch (Win32Exception ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new InputHostUnavailableException("无法启动管理员输入宿主：" + ex.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var client = new InputHostClient(pipe, process);
            var ping = await client.SendAsync(new InputRequest("ping"), cancellationToken).ConfigureAwait(false);
            if (!ping.Ok) throw new InputHostUnavailableException("管理员输入宿主未通过握手：" + ping.Error);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new InputHostUnavailableException("等待管理员输入宿主超时（45 秒）。");
        }
        catch (UnauthorizedAccessException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new InputHostUnavailableException("管理员输入宿主无法连接命名管道：" + ex.Message);
        }
    }

    public async Task<InputResponse> SendAsync(InputRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var line = JsonSerializer.Serialize(request, InputHostProtocol.Json);
            await _writer.WriteLineAsync(line).ConfigureAwait(false);
            var responseLine = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (responseLine is null) throw new InputHostUnavailableException("管理员输入宿主已断开连接。");
            return JsonSerializer.Deserialize<InputResponse>(responseLine, InputHostProtocol.Json)
                ?? throw new InputHostUnavailableException("管理员输入宿主返回了空响应。");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync(new InputRequest("shutdown"), timeout.Token).ConfigureAwait(false);
        }
        catch { }
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
        try
        {
            if (_process is { HasExited: false })
            {
                if (!_process.WaitForExit(3000)) Processes.WindowsProcessSupervisor.TerminateOwnedProcess(_process);
            }
            _process?.Dispose();
        }
        catch { }
    }
}
