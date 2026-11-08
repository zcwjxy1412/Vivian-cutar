using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Diagnostics;

namespace VIvian.App;

/// <summary>
/// Turns a startup failure into something the user can act on: a plain-language reason in the message box and a
/// local report file. Reports contain VIvian's own version, paths and the redacted exception; no account data.
/// </summary>
internal static class StartupDiagnostics
{
    public const string FileName = "VIvian-启动失败报告.txt";

    public static string Explain(Exception ex) => ex switch
    {
        _ when DatabaseVersionTooNew.TryRead(ex, out var found, out var supported) =>
            $"数据目录里的数据库是 v{found}，说明你之前用过更新的 VIvian；本程序只支持到 v{supported}。\n" +
            "请改用更新的 VIvian 版本打开。为保护数据，本程序没有修改数据库。",
        SqliteException s => $"本地数据库无法打开（SQLite 错误 {s.SqliteErrorCode}）。可能是磁盘空间不足、文件被占用或文件损坏。原数据不会被删除。",
        UnauthorizedAccessException => "没有权限读写 VIvian 的数据目录。请确认不是以其他用户身份运行，且该目录未被设为只读。",
        IOException io => "读写数据目录失败：" + io.Message,
        InvalidOperationException op when op.Message.Contains("标记", StringComparison.Ordinal) =>
            op.Message + "\n这说明程序包不完整或与数据目录不匹配，请重新解压完整的程序包。",
        _ => $"{ex.GetType().Name}：{ex.Message}",
    };

    /// <summary>Writes the report to the data directory, the program folder and %TEMP%; returns the first path written.</summary>
    public static string? Write(Exception ex, string? dataRoot, string stage)
    {
        string text;
        try { text = Build(ex, dataRoot, stage); }
        catch { text = $"{DateTimeOffset.Now:O} 启动失败（阶段 {stage}）：{ex.GetType().Name}"; }
        string? first = null;
        foreach (var directory in new[] { dataRoot, AppContext.BaseDirectory, Path.GetTempPath() })
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, FileName);
                File.WriteAllText(path, text, new UTF8Encoding(true));
                first ??= path;
            }
            catch { /* try the next location */ }
        }
        return first;
    }

    private static string Build(Exception ex, string? dataRoot, string stage)
    {
        var redactor = Redactor.ForCurrentUser();
        var builder = new StringBuilder();
        builder.AppendLine("VIvian 启动失败报告");
        builder.AppendLine($"时间: {DateTimeOffset.Now:O}");
        builder.AppendLine($"阶段: {stage}");
        builder.AppendLine($"版本: {AppPaths.Version}  提交: {AppPaths.Commit}  构建: {AppPaths.BuildTime}");
        builder.AppendLine($"程序位置: {redactor.Redact(AppContext.BaseDirectory)}");
        builder.AppendLine($"数据目录: {(dataRoot is null ? "（尚未确定）" : redactor.Redact(dataRoot))}");
        builder.AppendLine($"系统: {Environment.OSVersion.VersionString}  64位进程: {Environment.Is64BitProcess}");
        builder.AppendLine($"进程: {Environment.ProcessId}");
        var marker = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.marker").Select(Path.GetFileName);
        builder.AppendLine($"标记文件: {string.Join(", ", marker)}");
        if (dataRoot is not null)
        {
            var db = Path.Combine(dataRoot, "vivian.db");
            builder.AppendLine($"数据库: {(File.Exists(db) ? new FileInfo(db).Length + " 字节" : "不存在")}");
            if (File.Exists(db)) builder.AppendLine($"数据库版本: {TryReadVersion(db)}（本程序支持到 v{LocalDatabase.CurrentSchemaVersion}）");
        }
        builder.AppendLine();
        builder.AppendLine("原因说明: " + Explain(ex));
        builder.AppendLine();
        builder.AppendLine("技术细节（已去除用户名等个人信息）:");
        builder.AppendLine(redactor.Redact(ex.ToString()));
        return builder.ToString();
    }

    /// <summary>Read-only peek at the schema version; never migrates or locks for write.</summary>
    private static string TryReadVersion(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return "v" + Convert.ToInt32(command.ExecuteScalar());
        }
        catch (Exception e) { return "无法读取（" + e.GetType().Name + "）"; }
    }
}
