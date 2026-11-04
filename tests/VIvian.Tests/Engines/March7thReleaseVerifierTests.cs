using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VIvian.Core;
using VIvian.Infrastructure.Engines;
using Xunit;

namespace VIvian.Tests.Engines;

/// <summary>
/// Synthetic archives only. The pinned official table is replaced by a test table whose digest is computed from the
/// synthetic bytes, so these tests prove the checking logic — not anything about the real upstream release.
/// </summary>
public sealed class March7thReleaseVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-m7a-release-" + Guid.NewGuid().ToString("N"));
    public March7thReleaseVerifierTests() => Directory.CreateDirectory(_root);
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private const string Root = "March7thAssistant_full/";

    private string WriteZip(string name, IDictionary<string, string> entries)
    {
        var path = Path.Combine(_root, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (entry, content) in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false)))
                    writer.Write(content);
        return path;
    }

    private static Dictionary<string, string> Layout(string version = "v2026.10.31", string cli = "cli bytes") => new()
    {
        [Root + "m7a.exe"] = cli,
        [Root + "March7th Assistant.exe"] = "main bytes",
        [Root + "assets/config/version.txt"] = version + "\n",
    };

    private static March7thOfficialAsset Pin(string path, string name = "March7thAssistant_full.zip", bool full = true) =>
        new("v2026.10.31", name, new FileInfo(path).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), full);

    [Fact]
    public async Task ExactOfficialZipYieldsTheCliHashFromInsideTheArchive()
    {
        var zip = WriteZip("March7thAssistant_full.zip", Layout());
        var result = await March7thReleaseVerifier.VerifyAsync(zip, [Pin(zip)], default);
        Assert.Equal(ReleaseCheck.Verified, result.Check);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("cli bytes"u8.ToArray())).ToLowerInvariant(), result.Evidence!.CliSha256);
        Assert.Equal("v2026.10.31", result.Evidence.Version);
        Assert.Single(Directory.GetFiles(_root)); // nothing was extracted
    }

    [Fact]
    public async Task OneChangedByteIsNotOfficial()
    {
        var zip = WriteZip("March7thAssistant_full.zip", Layout());
        var pin = Pin(zip);
        var bytes = await File.ReadAllBytesAsync(zip);
        bytes[^10] ^= 0x01; // same length, different content
        await File.WriteAllBytesAsync(zip, bytes);
        var result = await March7thReleaseVerifier.VerifyAsync(zip, [pin], default);
        Assert.Equal(ReleaseCheck.NotOfficial, result.Check);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task OfficialSevenZipIsRecognisedButCannotVerifyInstalledFiles()
    {
        var path = Path.Combine(_root, "March7thAssistant_full.7z");
        await File.WriteAllBytesAsync(path, "7z synthetic bytes"u8.ToArray());
        var result = await March7thReleaseVerifier.VerifyAsync(path, [Pin(path, "March7thAssistant_full.7z")], default);
        Assert.Equal(ReleaseCheck.OfficialContentsUnavailable, result.Check);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task WrongVersionFileOrMissingCliIsUnexpectedLayout()
    {
        var wrongVersion = WriteZip("a.zip", Layout(version: "v2025.1.1"));
        Assert.Equal(ReleaseCheck.UnexpectedLayout, (await March7thReleaseVerifier.VerifyAsync(wrongVersion, [Pin(wrongVersion)], default)).Check);
        var layout = Layout();
        layout.Remove(Root + "m7a.exe");
        var noCli = WriteZip("b.zip", layout);
        Assert.Equal(ReleaseCheck.UnexpectedLayout, (await March7thReleaseVerifier.VerifyAsync(noCli, [Pin(noCli)], default)).Check);
    }

    [Fact]
    public async Task PathTraversalEntriesAreRejectedWithoutExtracting()
    {
        var layout = Layout();
        layout["../outside.txt"] = "x";
        var zip = WriteZip("c.zip", layout);
        var result = await March7thReleaseVerifier.VerifyAsync(zip, [Pin(zip)], default);
        Assert.Equal(ReleaseCheck.UnexpectedLayout, result.Check);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
    }

    [Fact]
    public async Task BackslashSeparatorsAndDoubleDotsInsideNamesAreAccepted()
    {
        var layout = Layout().ToDictionary(kv => kv.Key.Replace('/', '\\'), kv => kv.Value);
        layout[Root + "3rdparty/tool..v2/readme.txt"] = "not an escape";
        var zip = WriteZip("d.zip", layout);
        Assert.Equal(ReleaseCheck.Verified, (await March7thReleaseVerifier.VerifyAsync(zip, [Pin(zip)], default)).Check);
    }

    [Theory]
    [InlineData("/abs/file.txt")]
    [InlineData("March7thAssistant_full\\..\\..\\escape.txt")]
    [InlineData("C:/windows/escape.txt")]
    public async Task EscapingEntriesAreRejected(string escaping)
    {
        var layout = Layout();
        layout[escaping] = "x";
        var zip = WriteZip("e.zip", layout);
        Assert.Equal(ReleaseCheck.UnexpectedLayout, (await March7thReleaseVerifier.VerifyAsync(zip, [Pin(zip)], default)).Check);
    }

    [Fact]
    public async Task PinnedOfficialTableMatchesTheReleaseAudit()
    {
        // Values recorded from the GitHub release metadata in docs/evidence/M3_MARCH7TH_RELEASE_ASSET_AUDIT.md.
        var full = Assert.Single(March7thReleaseVerifier.OfficialAssets, a => a.FileName == "March7thAssistant_full.zip");
        Assert.Equal("e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985", full.Sha256);
        Assert.Equal(719711875, full.Length);
        // A file that is not the official asset can never verify against the real table.
        var zip = WriteZip("March7thAssistant_full.zip", Layout());
        Assert.Equal(ReleaseCheck.NotOfficial, (await March7thReleaseVerifier.VerifyAsync(zip)).Check);
    }

    [Fact]
    public async Task OtherEnginesAreFoundButNeverRunnable()
    {
        var dir = Path.Combine(_root, "bgi");
        Directory.CreateDirectory(dir);
        Assert.Equal(DiscoveryState.NotInstalled, (await EngineFileDiscovery.InspectAsync(GameId.Genshin, dir)).State);
        await File.WriteAllTextAsync(Path.Combine(dir, "BetterGI.exe"), "harmless");
        var found = await EngineFileDiscovery.InspectAsync(GameId.Genshin, dir);
        Assert.Equal(DiscoveryState.Found, found.State);
        Assert.EndsWith("BetterGI.exe", found.ProgramPath);
        Assert.Equal(DiscoveryState.NotInstalled, (await EngineFileDiscovery.InspectAsync(GameId.ZenlessZoneZero, "relative")).State);
    }
}
