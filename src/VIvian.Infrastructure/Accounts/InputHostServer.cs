using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VIvian.Infrastructure.Accounts;

/// <summary>
/// Runs inside a hidden, elevated instance of VIvian. The game runs at high integrity, so a normal-integrity
/// process cannot deliver clicks to it (UIPI). The host only acts on the Star Rail window and process.
/// </summary>
public static class InputHostServer
{
    public static int Run(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) return 2;
        Log($"run pipe={pipeName}");
        WindowsNative.EnsureDpiAwareness();
        try
        {
            // The medium-integrity client owns the pipe server, so this elevated instance connects as a
            // client. This direction avoids the high mandatory label blocking the client's write-up.
            using var server = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            Log("client connecting; waiting");
            server.Connect(45_000);
            Log("server connected");
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            while (server.IsConnected)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                InputRequest? request;
                try { request = JsonSerializer.Deserialize<InputRequest>(line, InputHostProtocol.Json); }
                catch (JsonException) { writer.WriteLine(Serialize(InputResponse.Failure("请求格式无效。"))); continue; }
                if (request is null) { writer.WriteLine(Serialize(InputResponse.Failure("请求为空。"))); continue; }
                if (string.Equals(request.Op, "shutdown", StringComparison.Ordinal))
                {
                    writer.WriteLine(Serialize(InputResponse.Success("shutdown")));
                    break;
                }
                var response = Execute(request);
                writer.WriteLine(Serialize(response));
            }
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                var log = Path.Combine(Path.GetTempPath(), "vivian-input-host.log");
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} {ex}\n");
            }
            catch { }
            return 1;
        }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "vivian-host-boot.log"), $"{DateTimeOffset.Now:O} host: {message}\n"); }
        catch { }
    }

    private static string Serialize(InputResponse response) =>
        JsonSerializer.Serialize(response, InputHostProtocol.Json);

    private static InputResponse Execute(InputRequest request)
    {
        try
        {
            return request.Op switch
            {
                "ping" => InputResponse.Success($"input-host pid={Environment.ProcessId}"),
                "foreground" => Foreground(request),
                "click" => Click(request),
                "kill" => Kill(request),
                "start" => StartGame(request),
                _ => InputResponse.Failure($"不支持的操作：{request.Op}"),
            };
        }
        catch (Exception ex)
        {
            return InputResponse.Failure(ex.GetBaseException().Message);
        }
    }

    private static InputResponse Foreground(InputRequest request)
    {
        var handle = new IntPtr(request.Hwnd);
        if (!IsStarRailWindow(handle, request.ProcessId, out var error)) return InputResponse.Failure(error!);
        var active = StarRailWindow.BringToFront(handle);
        return InputResponse.Success(active ? "foreground" : "not-foreground");
    }

    private static InputResponse Click(InputRequest request)
    {
        var handle = new IntPtr(request.Hwnd);
        if (!IsStarRailWindow(handle, request.ProcessId, out var error)) return InputResponse.Failure(error!);
        if (!WindowsNative.GetClientRect(handle, out var client) || client.Width <= 0 || client.Height <= 0)
            return InputResponse.Failure("无法读取游戏窗口客户区。");
        if (request.X < 0 || request.Y < 0 || request.X >= client.Width || request.Y >= client.Height)
            return InputResponse.Failure($"点击坐标超出游戏窗口：({request.X},{request.Y}) / {client.Width}x{client.Height}");

        StarRailWindow.BringToFront(handle);
        Thread.Sleep(150);
        var point = new WindowsNative.Point(request.X, request.Y);
        if (!WindowsNative.ClientToScreen(handle, ref point)) return InputResponse.Failure("客户区坐标转换失败。");

        // Never click through another window: a UAC prompt, the launcher or a browser may be on top.
        var target = WindowsNative.GetAncestor(WindowsNative.WindowFromPoint(point), WindowsNative.GaRoot);
        if (target != handle)
        {
            StarRailWindow.BringToFront(handle);
            Thread.Sleep(200);
            target = WindowsNative.GetAncestor(WindowsNative.WindowFromPoint(point), WindowsNative.GaRoot);
            if (target != handle)
                return InputResponse.Failure($"点击位置被其他窗口遮挡（期望 {handle}，实际 {target}）。");
        }

        if (!WindowsNative.SetCursorPos(point.X, point.Y)) return InputResponse.Failure("无法移动鼠标。");
        Thread.Sleep(80);
        WindowsNative.mouse_event(WindowsNative.MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        WindowsNative.mouse_event(WindowsNative.MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        return InputResponse.Success($"click {point.X},{point.Y}");
    }

    private static InputResponse Kill(InputRequest request) =>
        Processes.WindowsProcessSupervisor.TerminateStarRail(request.ProcessId, request.ProcessStartUtcTicks, out var detail)
            ? InputResponse.Success(detail)
            : InputResponse.Failure(detail);

    private static InputResponse StartGame(InputRequest request)
    {
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !string.Equals(Path.GetFileName(path), "StarRail.exe", StringComparison.OrdinalIgnoreCase))
            return InputResponse.Failure("拒绝启动：路径不是 StarRail.exe。");
        try
        {
            if (StarRailWindow.IsProcessRunning()) return InputResponse.Success("already-running");
            using var process = Processes.WindowsProcessSupervisor.StartUserProcess(path, Path.GetDirectoryName(path)!);
            return process is null ? InputResponse.Failure("Windows 未返回已启动的星铁进程。")
                : InputResponse.Success($"started {process.Id}");
        }
        catch (Exception ex) { return InputResponse.Failure("启动星铁失败：" + ex.Message); }
    }

    private static bool IsStarRailWindow(IntPtr handle, int expectedProcessId, out string? error)
    {
        error = null;
        if (handle == IntPtr.Zero || !WindowsNative.IsWindow(handle))
        {
            error = "游戏窗口句柄无效或已关闭。";
            return false;
        }
        WindowsNative.GetWindowThreadProcessId(handle, out var processId);
        if (expectedProcessId != 0 && processId != (uint)expectedProcessId)
        {
            error = "窗口进程与请求不一致。";
            return false;
        }
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.ProcessName, StarRailWindow.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                error = "拒绝操作非星铁窗口。";
                return false;
            }
        }
        catch (ArgumentException)
        {
            error = "窗口所属进程已退出。";
            return false;
        }
        return true;
    }
}
