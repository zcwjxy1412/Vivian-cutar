using System.Text;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.OneDragon;

internal static class OneDragonBootDiagnostics
{
    internal static async Task<ReadinessCheck> CheckoutAsync(string directory, CancellationToken ct)
    {
        const string id = "od:checkout";
        const string title = "源码初始化";
        try
        {
            var git = Path.Combine(directory, ".git");
            var attributes = await ReadOnlyFileAccess.BoundedAsync(() => File.GetAttributes(git),
                ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new(id, title, CheckState.Failed, EngineErrorCategory.LinkRejected, ".git 是链接，未读取。");
            return new(id, title, CheckState.Pending, EngineErrorCategory.EvidencePending,
                "发现 .git。仍需核对配置中的本地分支、实际提交及源码和资源内容。");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(id, title, CheckState.Failed, EngineErrorCategory.NotConfigured,
                "没有本地 Git 检出。固定版启动器首次运行会尝试联网同步，即使已解压源码。");
        }
        catch (TimeoutException)
        {
            return new(id, title, CheckState.Failed, EngineErrorCategory.Timeout, "读取本地 Git 检出状态超时。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(id, title, CheckState.Failed, EngineErrorCategory.AccessDenied, "无法读取本地 Git 检出状态。");
        }
    }

    internal static async Task<ReadinessCheck> UpdatesAsync(string directory, CancellationToken ct)
    {
        const string id = "od:update";
        const string title = "代码自动更新";
        try
        {
            var env = await OneDragonConfigRead.ReadAsync(directory, "env.yml", ct).ConfigureAwait(false);
            if (env is not null)
            {
                var lines = OneDragonConfigRead.Lines(env);
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in lines)
                {
                    var key = OneDragonConfigRead.KeyOf(line);
                    if (key is null || !keys.Add(key) || YamlScalarEditor.Locate([line], key) is null)
                        return new(id, title, CheckState.Failed, EngineErrorCategory.UnsafeLayout, "无法可靠识别 env.yml 中的更新设置。");
                }
                if (YamlScalarEditor.Locate(lines, "auto_update_code") is { Quote: null, Value: "false" })
                    return new(id, title, CheckState.Passed, EngineErrorCategory.None,
                        "配置明确关闭代码自动更新。首次同步仍取决于源码初始化。");
            }
            return new(id, title, CheckState.Failed, EngineErrorCategory.UnsafeLayout,
                "未确认关闭代码自动更新。缺少设置时，上游默认启用。", "在隔离安装中关闭代码自动更新并核对源码。");
        }
        catch (OneDragonConfigLinkException)
        {
            return new(id, title, CheckState.Failed, EngineErrorCategory.LinkRejected, "env.yml 的路径含链接，未读取。");
        }
        catch (TimeoutException)
        {
            return new(id, title, CheckState.Failed, EngineErrorCategory.Timeout, "读取更新设置超时。");
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            return new(id, title, CheckState.Failed, EngineErrorCategory.UnsafeLayout, "env.yml 超过读取限制或编码无效。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(id, title, CheckState.Failed, EngineErrorCategory.AccessDenied, "无法读取更新设置。");
        }
    }

    internal static ReadinessCheck Instances(OneDragonInstanceList catalog)
    {
        const string id = "od:instances";
        const string title = "脚本实例";
        return catalog.Status switch
        {
            OneDragonInstanceStatus.Available => new(id, title, CheckState.Passed, EngineErrorCategory.None,
                $"读取到 {catalog.Instances.Count} 个已保存的脚本实例。游戏账号身份需要单独核对。"),
            OneDragonInstanceStatus.Missing or OneDragonInstanceStatus.Empty =>
                new(id, title, CheckState.Failed, EngineErrorCategory.NotConfigured, "没有已保存的 OneDragon 脚本实例。"),
            OneDragonInstanceStatus.LinkRejected =>
                new(id, title, CheckState.Failed, EngineErrorCategory.LinkRejected, "实例清单的路径含链接，未读取。"),
            OneDragonInstanceStatus.Unavailable =>
                new(id, title, CheckState.Failed, EngineErrorCategory.AccessDenied, "无法读取实例清单。"),
            _ => new(id, title, CheckState.Failed, EngineErrorCategory.UnsafeLayout, "实例清单格式无效或超出支持范围。"),
        };
    }
}
