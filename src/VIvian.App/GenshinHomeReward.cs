using System.IO;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines;

namespace VIvian.App;

internal sealed class GenshinHomeReward(string officialDirectory, Func<string, CancellationToken, Task> prepareSnapshot,
    GenshinDailyVerification verify, Func<string, LocalDatabase>? openSnapshot = null)
{
    private readonly string _root = Path.Combine(Path.GetDirectoryName(officialDirectory)!,
        UnifiedProfilePreparation.GenshinHomeRewardFolder);

    internal GenshinRewardAcceptance CreateRequest(GameAccount account)
    {
        if (account is not { Game: GameId.Genshin, Enabled: true, LoginState: "recognized" } || account.Id == Guid.Empty)
            throw new InvalidOperationException("请选择一个已启用并绑定登录标识的原神账号。");
        return new(Guid.NewGuid(), account.Id, account.Revision, _root);
    }

    internal async Task<MultiAccountOutcome> RunAsync(GenshinRewardAcceptance request, string betterGiRoot, string archivePath,
        Action<BatchAccountProgress>? progress, CancellationToken ct)
    {
        var destination = Path.Combine(_root, request.Attempt.ToString("N"));
        if (!string.Equals(destination, request.ProfileDirectory, StringComparison.OrdinalIgnoreCase) || request.ReviewOnly)
            throw new InvalidOperationException("原神首跑请求与隔离档案不一致。");
        if (ReadLatest() is { } prior && (await prior.ReadUnresolvedAsync(ct).ConfigureAwait(false)).Any(IsAcceptanceRun))
            throw new InvalidOperationException("上一轮原神首跑仍有待核对的运行记录。");
        await prepareSnapshot(destination, ct).ConfigureAwait(false);
        return await request.RunAsync(Open(destination), betterGiRoot, archivePath, destination, verify, progress, ct,
            recoveryProfileDirectory: officialDirectory, beforeVerify: () => WriteLatest(request.Attempt)).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<RunRecord>> ReadRecentRunsAsync(int limit)
    {
        if (ReadLatest() is not { } database) return [];
        return (await database.ReadRecentRunsAsync(limit).ConfigureAwait(false)).Where(IsAcceptanceRun).ToArray();
    }

    internal async Task<bool> RecordRecoveryAsync(Guid runId, CancellationToken ct)
    {
        if (ReadLatest() is not { } database) return false;
        var runs = await database.ReadBoundRunsAsync(GameId.Genshin, accountId: null, limit: 20, ct).ConfigureAwait(false);
        if (runs.SingleOrDefault(row => row.RunId == runId && IsAcceptanceRun(row)) is not { } run) return false;
        if (run.IsUnresolved)
            await database.AcknowledgeAsync(runId, "VIvian 按本次原神首跑记录核对桌面交接与 BetterGI 协议恢复。",
                DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return true;
    }

    private static bool IsAcceptanceRun(RunRecord run) =>
        run.Game == GameId.Genshin && !run.IsSimulation && run.RuleVersion.EndsWith("/acceptance", StringComparison.Ordinal);

    private LocalDatabase Open(string directory) =>
        openSnapshot?.Invoke(directory) ?? new LocalDatabase(Path.Combine(directory, "vivian.db"), requirePreparedProfile: true);

    private LocalDatabase? ReadLatest()
    {
        var pointer = Path.Combine(_root, "latest");
        if (ReadOnlyFileAccess.FindReparsePoint(pointer) is not null)
            throw new IOException("原神首跑档案索引含链接。");
        if (!File.Exists(pointer)) return null;
        using var stream = ReadOnlyFileAccess.OpenForStableRead(pointer);
        if (stream.Length != 32 || !ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, pointer) ||
            ReadOnlyFileAccess.LinkCount(stream.SafeFileHandle) != 1)
            throw new IOException("无法核对原神首跑档案索引。");
        using var reader = new StreamReader(stream);
        if (!Guid.TryParseExact(reader.ReadToEnd(), "N", out var attempt) || attempt == Guid.Empty)
            throw new InvalidDataException("原神首跑档案索引无效。");
        return Open(Path.Combine(_root, attempt.ToString("N")));
    }

    private void WriteLatest(Guid attempt)
    {
        var pointer = Path.Combine(_root, "latest");
        var temporary = Path.Combine(_root, attempt.ToString("N") + ".latest");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, temporary) ||
                ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) != 1)
                throw new IOException("无法核对原神首跑档案索引的实际写入位置。");
            file.Write(System.Text.Encoding.ASCII.GetBytes(attempt.ToString("N")));
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, pointer, overwrite: true);
    }
}
