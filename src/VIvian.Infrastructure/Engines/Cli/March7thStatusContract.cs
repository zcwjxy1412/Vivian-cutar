using System.Text.Json;

namespace VIvian.Infrastructure.Engines.Cli;

/// <summary>解析 v2026.10.31 `m7a.exe --status` 的一次性快照。只处理协议字段，不启动上游程序。</summary>
public static class March7thStatusContract
{
    public static March7thStatusResult Parse(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 24 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
                || !schema.TryGetInt32(out var revision) || revision != 1)
                return March7thStatusResult.Block("状态协议版本不兼容。");
            if (!root.TryGetProperty("first_run_completed", out var first) || first.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("game_path_exists", out var path) || path.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("cloud_game_enable", out var cloud) || cloud.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("active_run", out var active) || active.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)
                || !root.TryGetProperty("elevation", out var elevation) || elevation.ValueKind != JsonValueKind.String)
                return March7thStatusResult.Block("状态缺少必需字段。");
            if (!first.GetBoolean()) return March7thStatusResult.Block("请先由本人确认上游免责声明。");
            if (!path.GetBoolean() && !cloud.GetBoolean()) return March7thStatusResult.Block("上游游戏路径不存在。");
            if (active.ValueKind != JsonValueKind.Null) return March7thStatusResult.Block("上游已有任务运行，禁止重复启动。");
            // 免 UAC、已提权与不需要提权都必须各自验证；本阶段只承认需用户亲自确认的 UAC 路径。
            if (elevation.GetString() != "uac") return March7thStatusResult.Block("权限路径尚未完成独立验证。");
            return new(true, "上游状态允许进入 UAC 验证；尚未证明跨权限进程可安全停止。", "uac");
        }
        catch (JsonException) { return March7thStatusResult.Block("状态不是有效 JSON。"); }
        catch (InvalidOperationException) { return March7thStatusResult.Block("状态字段格式无效。"); }
    }
}

public sealed record March7thStatusResult(bool ReadyForUacValidation, string Detail, string? Elevation)
{
    public static March7thStatusResult Block(string reason) => new(false, reason, null);
}
