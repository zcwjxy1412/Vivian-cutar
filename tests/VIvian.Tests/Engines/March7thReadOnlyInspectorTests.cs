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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
