using System.Security.Cryptography;
using System.Text;

namespace VIvian.Infrastructure.Engines.OneDragon;

internal static class OneDragonInstalledSource
{
    internal const string Branch = "vivian-v2.6.0";

    internal static async Task VerifyAsync(string root, CancellationToken ct)
    {
        var env = await OneDragonConfigRead.ReadAsync(root, "env.yml", ct).ConfigureAwait(false);
        var branch = env is null ? null : YamlScalarEditor.Locate(OneDragonConfigRead.Lines(env), "git_branch")?.Value;
        if (branch != Branch
            || await ReadGitRefAsync(root, "HEAD", ct).ConfigureAwait(false) != $"ref: refs/heads/{Branch}"
            || await ReadGitRefAsync(root, $"refs/heads/{Branch}", ct).ConfigureAwait(false) != OneDragonContract.PinnedCommit)
            throw new InvalidOperationException("OneDragon 当前源码检出与已核对的 v2.6.0 不一致。");

        var updates = Path.Combine(root, "src", "one_dragon", "envs", "update_service.py");
        if (ReadOnlyFileAccess.FindReparsePoint(updates) is not null || !File.Exists(updates))
            throw new InvalidOperationException("无法核对 OneDragon 的启动器版本保护。请重新准备固定版安装。");
        await using var file = ReadOnlyFileAccess.OpenForStableRead(updates);
        if (file.Length > 64 * 1024 || !ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, updates)
            || Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false))
                != "7D907F8E86037D942993529BA6037509270E0F2E657F66B82F3C49A275CC0999")
            throw new InvalidOperationException("OneDragon 的启动器版本保护已变化。请重新准备固定版安装。");
    }

    private static async Task<string> ReadGitRefAsync(string root, string relative, CancellationToken ct)
    {
        var path = Path.Combine(root, ".git", relative);
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidOperationException("OneDragon 的源码引用路径包含链接。");
        await using var file = ReadOnlyFileAccess.OpenForStableRead(path);
        if (file.Length > 256 || !ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path))
            throw new InvalidOperationException("无法核对 OneDragon 的源码引用。");
        using var reader = new StreamReader(file, Encoding.ASCII);
        return (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
    }
}
