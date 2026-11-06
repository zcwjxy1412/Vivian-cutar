namespace VIvian.Core;

public enum GameId { Genshin, StarRail, ZenlessZoneZero }
public sealed record EngineSetting(GameId Game, string InstallPath, long Revision);
public sealed record DiagnosticRecord(string Id, DateTimeOffset StartedAt, string Summary);

public interface ISettingsStore
{
    Task<IReadOnlyList<EngineSetting>> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(IReadOnlyList<EngineSetting> settings, CancellationToken ct = default);
    /// <summary>
    /// Updates one engine's install path only (optimistic revision check). Other engines' rows are untouched, so a
    /// path chosen on one page never overwrites a newer value saved for another engine.
    /// </summary>
    Task<EngineSetting> SaveSettingAsync(EngineSetting setting, CancellationToken ct = default);
}
public interface IDiagnosticStore
{
    Task AppendAsync(DiagnosticRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<DiagnosticRecord>> ReadRecentAsync(int limit, CancellationToken ct = default);
}
public sealed class SettingsConflictException() : Exception("配置已在其他窗口更新，请重新加载后再保存。");

public static class SettingsValidation
{
    public static void Validate(IReadOnlyList<EngineSetting> settings)
    {
        if (settings.Count != 3 || settings.Select(x => x.Game).Distinct().Count() != 3)
            throw new ArgumentException("必须包含三款游戏各一项设置。");
        foreach (var item in settings)
        {
            if (!Enum.IsDefined(item.Game) || item.Revision < 0)
                throw new ArgumentException("配置标识或版本无效。");
            if (item.InstallPath.Length > 1024 || item.InstallPath.Any(char.IsControl))
                throw new ArgumentException("路径过长或包含控制字符。");
        }
    }
}
