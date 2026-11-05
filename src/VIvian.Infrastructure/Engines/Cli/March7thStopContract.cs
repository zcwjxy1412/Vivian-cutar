using System.Text.Json;

namespace VIvian.Infrastructure.Engines.Cli;

/// <summary>v2026.10.31 control_command 的 JSON 回执。acknowledged 只证明收到停止指令。</summary>
public static class March7thStopContract
{
    public static bool Acknowledged(string output, string expectedRunId)
    {
        var last = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).LastOrDefault(x => x.StartsWith('{'));
        if (last is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(last, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("run_id", out var id) && id.GetString() == expectedRunId
                && root.TryGetProperty("action", out var action) && action.GetString() == "stop"
                && root.TryGetProperty("acknowledged", out var acknowledged)
                && acknowledged.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
}
