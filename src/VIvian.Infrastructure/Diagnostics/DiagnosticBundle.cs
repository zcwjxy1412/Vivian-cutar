using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Diagnostics;

/// <summary>Removes VIvian's own old log files. Only files named like VIvian's logs are ever considered.</summary>
public static partial class LogMaintenance
{
    [GeneratedRegex(@"^vivian-\d{8}(_\d{3})?\.log$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LogName();

    public static bool IsVivianLog(string fileName) => LogName().IsMatch(fileName);

    /// <summary>Deletes logs older than <paramref name="retentionDays"/>, then the oldest until the total fits.
    /// The newest log is always kept; files in use are skipped. Returns the names deleted.</summary>
    public static IReadOnlyList<string> Prune(string directory, int retentionDays, long maxTotalBytes, DateTime nowUtc)
    {
        if (!Directory.Exists(directory)) return [];
        var logs = new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => IsVivianLog(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        var deleted = new List<string>();
        long total = 0;
        for (var i = 0; i < logs.Count; i++)
        {
            var file = logs[i];
            var tooOld = i > 0 && file.LastWriteTimeUtc < nowUtc.AddDays(-retentionDays);
            var tooBig = i > 0 && total + file.Length > maxTotalBytes;
            if (tooOld || tooBig)
            {
                try { file.Delete(); deleted.Add(file.Name); continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            total += file.Length;
        }
        return deleted;
    }
}

public sealed record DiagnosticBundleInput(
    string AppVersion, string DataDirectory, IReadOnlyList<EngineReadinessReport> Engines,
    IReadOnlyList<RunRecord> Runs, IReadOnlyList<DiagnosticRecord> SelfChecks, string? LogDirectory,
    bool IncludeRunHistory, DateTimeOffset CreatedAt);

public sealed record DiagnosticBundleResult(bool Success, string? Path, IReadOnlyList<string> Entries, string Detail);

/// <summary>
/// Builds a local, redacted ZIP for troubleshooting. It never contains the database file, upstream configuration
/// contents, credentials or account data, and it is never uploaded: the user decides whether to share it.
/// </summary>
public static class DiagnosticBundle
{
    public const int MaxLogFiles = 5;
    public const int MaxLogBytesPerFile = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<DiagnosticBundleResult> CreateAsync(DiagnosticBundleInput input, string destinationDirectory,
        Redactor redactor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Path.IsPathFullyQualified(destinationDirectory))
            return new(false, null, [], "导出目录必须是完整的本机路径。");
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            if (Engines.ReadOnlyFileAccess.FindReparsePoint(destinationDirectory) is not null)
                return new(false, null, [], "导出目录路径中含有链接，已取消导出。");
            var name = $"VIvian-diagnostics-{input.CreatedAt.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.zip";
            var final = Path.Combine(destinationDirectory, name);
            var temp = final + ".partial";
            var entries = new List<string>();
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                async Task AddText(string entryName, string text)
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
                    entries.Add(entryName);
                }
                await AddText("README.txt", Readme(input));
                await AddText("manifest.json", JsonSerializer.Serialize(new
                {
                    schema = "vivian.diagnostics/1",
                    appVersion = input.AppVersion,
                    createdAt = input.CreatedAt,
                    os = Environment.OSVersion.VersionString,
                    is64BitOs = Environment.Is64BitOperatingSystem,
                    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    processElevated = IsElevated(),
                    dataDirectory = redactor.Redact(input.DataDirectory),
                    realExecutionPermitted = RealExecutionPolicy.BuildPermitsRealExecution,
                }, Json));
                await AddText("engines.json", JsonSerializer.Serialize(input.Engines.Select(e => new
                {
                    e.Game, e.EngineName, e.PinnedVersion, e.Compatibility, e.DetectedVersion,
                    programPath = redactor.Redact(e.ProgramPath), e.ProgramSha256, e.CheckedAt,
                    checks = e.Checks.Select(c => new { c.Id, c.State, c.Category, message = redactor.Redact(c.Message) }),
                }), Json));
                if (input.IncludeRunHistory)
                    await AddText("runs.json", JsonSerializer.Serialize(input.Runs.Select(r => new
                    {
                        r.RunId, r.QueueId, r.Game, r.RuleVersion, r.IsSimulation, r.Phase, r.Outcome, r.Cleanup, r.ReasonCode,
                        r.CreatedAt, r.UpdatedAt, r.AcknowledgedAt, acknowledgementNote = redactor.Redact(r.AcknowledgementNote),
                    }), Json));
                await AddText("self-checks.json", JsonSerializer.Serialize(input.SelfChecks.Select(d => new
                { d.Id, d.StartedAt, summary = redactor.Redact(d.Summary) }), Json));
                foreach (var log in RecentLogs(input.LogDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    var text = await ReadTailAsync(log, ct);
                    if (text is null) continue;
                    var redacted = string.Join('\n', text.Split('\n').Select(redactor.Redact));
                    await AddText("logs/" + log.Name, redacted);
                }
            }
            File.Move(temp, final, overwrite: false);
            return new(true, final, entries, $"已导出诊断包（{entries.Count} 个条目），已脱敏，未包含数据库、上游配置或账号信息。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, null, [], $"导出失败（{ex.GetType().Name}）。请确认导出目录可写、磁盘空间足够。");
        }
    }

    private static IEnumerable<FileInfo> RecentLogs(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => LogMaintenance.IsVivianLog(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxLogFiles).ToList();
    }

    /// <summary>The last part of a log file, read while the logger may still be writing it.</summary>
    private static async Task<string?> ReadTailAsync(FileInfo file, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - MaxLogBytesPerFile);
            stream.Position = start;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct);
            return start > 0 ? "[已截断：只包含文件末尾]\n" + text : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool? IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string Readme(DiagnosticBundleInput input) => $"""
        VIvian 诊断包（{input.AppVersion}，{input.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC）

        包含：版本与系统信息、三款引擎的只读检查结果、{(input.IncludeRunHistory ? "运行记录（状态与原因代码）、" : "")}自检记录、最近的 VIvian 日志（末尾部分）。
        已脱敏：用户目录、用户名、计算机名、邮箱、口令/令牌样式的字段。
        不包含：数据库文件、任何引擎或游戏的配置内容、账号、密码、截图。

        这个文件只保存在本机。是否发送给他人由你决定；发送前可以先打开查看。
        说明：此开发版不运行真实引擎；“模拟”运行的结果不代表任何游戏的真实日常。
        """;
}
