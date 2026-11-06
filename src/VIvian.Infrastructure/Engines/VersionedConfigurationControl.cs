using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Simulation;

namespace VIvian.Infrastructure.Engines;

/// <summary>Exact-version registry and conservative, comment-preserving March7th scalar editor.</summary>
public sealed class VersionedConfigurationControl : IConfigurationControl
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Scalar = new(@"^(?<key>[a-z][a-z0-9_]*):[ \t]*(?<value>true|false)(?<tail>[ \t]*(?:\#.*)?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public IReadOnlyList<EngineTaskDefinition> ListTasks(GameId game) => EngineCatalog.For(game).Tasks;
    public EngineTaskDefinition GetTaskSchema(GameId game, string taskId) =>
        ListTasks(game).Single(t => t.Id == taskId);

    public ConfigurationSnapshot ReadConfiguration(GameId game, string installDirectory)
    {
        var profile = EngineCatalog.For(game);
        var root = Path.GetFullPath(installDirectory);
        if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("安装目录不存在或是链接目录。");
        var relative = game switch
        {
            GameId.StarRail => "config.yaml",
            GameId.Genshin => Path.Combine("User", "config.json"),
            GameId.ZenlessZoneZero => "config",
            _ => throw new ArgumentOutOfRangeException(nameof(game)),
        };
        var path = Path.Combine(root, relative);
        if (game == GameId.StarRail)
        {
            var version = Path.Combine(root, "assets", "config", "version.txt");
            if (!File.Exists(version) || File.ReadAllText(version, Utf8).Trim() != profile.PinnedVersion)
                throw new InvalidOperationException("星铁上游版本与固定版本不符。");
        }
        if (game == GameId.ZenlessZoneZero)
            return new(game, profile.PinnedVersion, path, "实例未绑定", profile.Parameters.Select(p =>
                new ConfigurationValue(p, null, Directory.Exists(path) ? "需先选择账号实例；只读" : "配置目录未找到")).ToArray(), DateTimeOffset.UtcNow);
        if (!File.Exists(path))
            return new(game, profile.PinnedVersion, path, "缺失", profile.Parameters.Select(p =>
                new ConfigurationValue(p, null, "配置文件未找到；只读")).ToArray(), DateTimeOffset.UtcNow);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("配置文件是链接，拒绝读取。");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 4_000_000) throw new IOException("配置文件超过安全读取上限。");
        var text = Utf8.GetString(bytes);
        var betterGiExe = Path.Combine(root, "BetterGI.exe");
        var betterGiVersionMatches = game != GameId.Genshin ||
            (File.Exists(betterGiExe) && System.Diagnostics.FileVersionInfo.GetVersionInfo(betterGiExe).FileVersion?.StartsWith("0.66.0", StringComparison.Ordinal) == true);
        var values = profile.Parameters.Select(p =>
        {
            if (game != GameId.StarRail) return new ConfigurationValue(p, null,
                betterGiVersionMatches ? "固定版本匹配；由原软件管理" : "版本未匹配或无法确认；只读");
            var matches = Lines(text).Select(line => Scalar.Match(line)).Where(m => m.Success &&
                m.Groups["key"].Value == p.Path).ToArray();
            if (p.DataType == "boolean")
                return matches.Length == 1
                    ? new ConfigurationValue(p, matches[0].Groups["value"].Value.ToLowerInvariant(),
                        p.Access == ParameterAccess.VerifiedReadWrite ? "可编辑" : "只读")
                    : new ConfigurationValue(p, null, "字段缺失、重复或格式不兼容；只读");
            return new ConfigurationValue(p, null, "由原软件管理；只读");
        }).ToArray();
        return new(game, profile.PinnedVersion, path, Convert.ToHexString(SHA256.HashData(bytes)), values, DateTimeOffset.UtcNow);
    }

    public ConfigurationProposal ProposeChanges(ConfigurationSnapshot snapshot, IReadOnlyDictionary<string, string> values)
    {
        var changes = values.Select(kv =>
        {
            var current = snapshot.Values.SingleOrDefault(v => v.Schema.Path == kv.Key)
                ?? throw new ArgumentException("未知参数：" + kv.Key);
            if (current.Schema.Access != ParameterAccess.VerifiedReadWrite || current.Value is null)
                throw new InvalidOperationException("此参数不允许写入：" + kv.Key);
            return new ConfigurationChange(kv.Key, current.Value, kv.Value.Trim().ToLowerInvariant());
        }).Where(c => c.OldValue != c.NewValue).ToArray();
        return new(snapshot, changes);
    }

    public ConfigurationValidation ValidateChanges(ConfigurationProposal proposal, bool engineRunning)
    {
        if (engineRunning) return new(false, "上游程序或 VIvian 任务正在运行，禁止写入。");
        if (proposal.Base.Game != GameId.StarRail || proposal.Base.PinnedVersion != EngineCatalog.March7th.PinnedVersion)
            return new(false, "该引擎版本不支持配置写入。");
        if (proposal.Changes.Count == 0) return new(false, "没有需要保存的变化。");
        if (proposal.Changes.Any(c => c.NewValue is not ("true" or "false") ||
            proposal.Base.Values.Single(v => v.Schema.Path == c.Path).Schema.Access != ParameterAccess.VerifiedReadWrite))
            return new(false, "参数类型或合法值不匹配。");
        if (!File.Exists(proposal.Base.Path)) return new(false, "配置文件已不存在。");
        var root = Path.GetDirectoryName(proposal.Base.Path)!;
        if (!string.Equals(Path.GetFullPath(proposal.Base.Path), Path.Combine(root, "config.yaml"), StringComparison.OrdinalIgnoreCase))
            return new(false, "配置路径不符合固定版本结构。");
        if (FakeEngineAdapter.ForeignProcessIds(Path.Combine(root, "m7a.exe")).Count > 0 ||
            FakeEngineAdapter.ForeignProcessIds(Path.Combine(root, "March7th Assistant.exe")).Count > 0)
            return new(false, "检测到上游程序正在运行；请退出后再保存。");
        return new(true, "可以保存；写入前仍会核对文件版本和运行状态。");
    }

    public ConfigurationApplyResult ApplyChanges(ConfigurationProposal proposal, string auditDirectory, bool engineRunning)
    {
        var validation = ValidateChanges(proposal, engineRunning);
        if (!validation.Valid) throw new InvalidOperationException(validation.Reason);
        WriteGate.Wait();
        try
        {
            var path = proposal.Base.Path;
            var current = ReadConfiguration(GameId.StarRail, Path.GetDirectoryName(path)!);
            if (current.Revision != proposal.Base.Revision) throw new IOException("配置已被外部修改；请重新读取并处理冲突。");
            var bytes = File.ReadAllBytes(path);
            var original = Utf8.GetString(bytes);
            var updated = original;
            foreach (var change in proposal.Changes)
            {
                var pattern = new Regex(@"(?m)^(?<prefix>" + Regex.Escape(change.Path) + @":[ \t]*)" +
                    Regex.Escape(change.OldValue) + @"(?<tail>[ \t]*(?:\#.*)?\r?)$", RegexOptions.CultureInvariant);
                if (pattern.Matches(updated).Count != 1) throw new IOException("字段结构已变化，拒绝写入。");
                updated = pattern.Replace(updated, m => m.Groups["prefix"].Value + change.NewValue + m.Groups["tail"].Value, 1);
            }
            Directory.CreateDirectory(auditDirectory);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
            var backup = Path.Combine(auditDirectory, "m7a-config-" + stamp + ".bak");
            var audit = Path.Combine(auditDirectory, "m7a-config-" + stamp + ".audit.txt");
            var temporary = path + ".vivian-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, Utf8.GetBytes(updated));
                if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != proposal.Base.Revision)
                    throw new IOException("配置在保存前被外部修改；请重新读取。");
                var lastCheck = ValidateChanges(proposal, engineRunning);
                if (!lastCheck.Valid) throw new IOException(lastCheck.Reason);
                File.Replace(temporary, path, backup);
                var reread = ReadConfiguration(GameId.StarRail, Path.GetDirectoryName(path)!);
                if (proposal.Changes.Any(c => reread.Values.Single(v => v.Schema.Path == c.Path).Value != c.NewValue))
                    throw new IOException("写入后读取的参数与预期不符；备份已保留。");
                File.WriteAllLines(audit, proposal.Changes.Select(c => $"{stamp} {c.Path}: {c.OldValue} -> {c.NewValue}; before={proposal.Base.Revision}; after={reread.Revision}"), Utf8);
                return new(reread, backup, audit);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { WriteGate.Release(); }
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(x => x.TrimEnd('\r'));
}
