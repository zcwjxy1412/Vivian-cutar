using VIvian.Infrastructure.Engines;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class March7thReadOnlyInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-m7a-inspect-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingRootAndCliRemainUnavailable()
    {
        var absent = await March7thReadOnlyInspector.InspectAsync(_root);
        Assert.Equal(March7thCompatibility.NotInstalled, absent.Compatibility);
        Assert.Null(absent.Sha256);

        Directory.CreateDirectory(_root);
        var missing = await March7thReadOnlyInspector.InspectAsync(_root);
        Assert.Equal(March7thCompatibility.MissingCli, missing.Compatibility);
        Assert.False(missing.CanQueryStatus);
        Assert.False(missing.CanQueryTasks);
    }

    [Fact]
    public async Task ArbitraryExecutableBytesAreHashedButNeverTrustedOrExecuted()
    {
        Directory.CreateDirectory(_root);
        var cli = Path.Combine(_root, "m7a.exe");
        await File.WriteAllTextAsync(cli, "harmless test bytes");
        var before = await File.ReadAllBytesAsync(cli);

        var result = await March7thReadOnlyInspector.InspectAsync(_root);

        Assert.Equal(March7thCompatibility.Unverified, result.Compatibility);
        Assert.Equal("ab8d086c47edf52bf89163880b2768e53154580566321694a1c89e0be4d08bd7", result.Sha256);
        Assert.False(result.CanQueryStatus);
        Assert.False(result.CanQueryTasks);
        Assert.Equal(before, await File.ReadAllBytesAsync(cli));
    }

    [Fact]
    public async Task RelativePathIsRejectedBeforeFileAccess()
    {
        var result = await March7thReadOnlyInspector.InspectAsync("relative-install");
        Assert.Equal(March7thCompatibility.NotInstalled, result.Compatibility);
    }

    private async Task<string> CreateCliAsync(string content = "harmless test bytes")
    {
        Directory.CreateDirectory(_root);
        var cli = Path.Combine(_root, "m7a.exe");
        await File.WriteAllTextAsync(cli, content);
        return cli;
    }

    [Fact]
    public async Task LockedCliIsReportedConservativelyInsteadOfThrowing()
    {
        var cli = await CreateCliAsync();
        await using (new FileStream(cli, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await March7thReadOnlyInspector.InspectAsync(_root);
            Assert.Equal(March7thCompatibility.Unverified, result.Compatibility);
            Assert.Null(result.Sha256);
            Assert.Null(result.FileVersion);
            Assert.Contains("无法读取", result.Detail);
            Assert.False(result.CanQueryStatus);
        }
    }

    [Fact]
    public async Task CliChangedDuringReadIsNotTrusted()
    {
        await CreateCliAsync();
        var result = await March7thReadOnlyInspector.InspectAsync(_root,
            afterRead: path => File.AppendAllTextAsync(path, " tampered"), CancellationToken.None);
        Assert.Equal(March7thCompatibility.Unverified, result.Compatibility);
        Assert.Null(result.Sha256); // a hash of a file that changed underneath is never reported
        Assert.Contains("发生变化", result.Detail);
    }

    [Fact]
    public async Task CliDeletedDuringReadIsNotTrusted()
    {
        await CreateCliAsync();
        var result = await March7thReadOnlyInspector.InspectAsync(_root,
            afterRead: path => { File.Delete(path); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(March7thCompatibility.Unverified, result.Compatibility);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task ReparsePointCliIsNeitherFollowedNorHashed()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "real-target.bin");
        await File.WriteAllTextAsync(target, "target bytes");
        var cli = Path.Combine(_root, "m7a.exe");
        try
        {
            File.CreateSymbolicLink(cli, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating symbolic links needs a privilege on some Windows desktops; Windows CI and Linux can create them.
            return;
        }
        var result = await March7thReadOnlyInspector.InspectAsync(_root);
        Assert.Equal(March7thCompatibility.Unverified, result.Compatibility);
        Assert.Null(result.Sha256);
        Assert.Contains("重解析点", result.Detail);
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
