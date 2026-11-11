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
