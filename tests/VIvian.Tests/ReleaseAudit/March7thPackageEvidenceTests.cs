using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace VIvian.Tests.ReleaseAudit;

/// <summary>
/// Static, offline release gate. The fixture archives below are synthetic: passing these tests does not verify
/// any upstream release binary or authorize running m7a.exe.
/// </summary>
public sealed class March7thPackageEvidenceTests
{
    private const string Root = "March7thAssistant_full/";
    private static readonly string[] RequiredEntries =
    [
        Root + "m7a.exe",
        Root + "March7th Assistant.exe",
        Root + "March7th Launcher.exe",
        Root + "assets/config/version.txt",
    ];

    [Fact]
    public void MatchingDigestAndExpectedLayoutOnlyEstablishStaticPackageIdentity()
    {
        var bytes = MakeZip(RequiredEntries.ToDictionary(path => path,
            path => path.EndsWith("version.txt", StringComparison.Ordinal) ? "v2026.10.31\n" : "test bytes"));
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var evidence = Inspect(bytes, digest, "v2026.10.31");

        Assert.True(evidence.DigestMatches);
        Assert.True(evidence.LayoutMatches);
        Assert.Equal("v2026.10.31", evidence.VersionFile);
        Assert.False(evidence.CliExecuted);
    }

    [Fact]
    public void TamperedArchiveCannotPassEvenWhenNamesLookRight()
    {
        var bytes = MakeZip(RequiredEntries.ToDictionary(path => path, _ => "test bytes"));
        var evidence = Inspect(bytes, new string('0', 64), "v2026.10.31");
        Assert.False(evidence.DigestMatches);
        Assert.False(evidence.LayoutMatches);
    }

    [Fact]
    public void MissingCliOrWrongVersionCannotPassStaticLayout()
    {
        var bytes = MakeZip(RequiredEntries.Where(x => !x.EndsWith("m7a.exe", StringComparison.Ordinal))
            .ToDictionary(path => path, path => path.EndsWith("version.txt", StringComparison.Ordinal)
                ? "v2026.9.30\n" : "test bytes"));
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var evidence = Inspect(bytes, digest, "v2026.10.31");
        Assert.True(evidence.DigestMatches);
        Assert.False(evidence.LayoutMatches);
    }

    [Fact]
    public void UnexpectedPathTraversalRejectsArchiveWithoutExtractingIt()
    {
        var entries = RequiredEntries.ToDictionary(path => path,
            path => path.EndsWith("version.txt", StringComparison.Ordinal) ? "v2026.10.31\n" : "test bytes");
        entries[Root + "../outside.txt"] = "untrusted";
        var bytes = MakeZip(entries);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.False(Inspect(bytes, digest, "v2026.10.31").LayoutMatches);
    }

    private sealed record StaticEvidence(bool DigestMatches, bool LayoutMatches, string? VersionFile, bool CliExecuted);

    private static StaticEvidence Inspect(byte[] archive, string expectedDigest, string expectedVersion)
    {
        var matching = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()),
            Encoding.ASCII.GetBytes(expectedDigest.ToLowerInvariant()));
        if (!matching) return new(false, false, null, false);

        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var paths = zip.Entries.Select(x => x.FullName.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var versionEntry = zip.GetEntry(Root + "assets/config/version.txt");
        string? version = null;
        if (versionEntry is not null)
        {
            using var reader = new StreamReader(versionEntry.Open(), Encoding.UTF8);
            version = reader.ReadToEnd().Trim();
        }
        var safe = paths.All(path => path.StartsWith(Root, StringComparison.Ordinal)
            && !path.Split('/').Contains("..", StringComparer.Ordinal));
        return new(true, safe && RequiredEntries.All(paths.Contains) && version == expectedVersion, version, false);
    }

    private static byte[] MakeZip(IReadOnlyDictionary<string, string> entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (path, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return stream.ToArray();
    }
}
