using System.Text;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Processes;
using VIvian.Infrastructure.Simulation;
using Xunit;

namespace VIvian.Tests.Processes;

public sealed class CommandLineTests
{
    public static TheoryData<string> TrickyArguments() => new()
    {
        "plain", "", "with space", "E:\\游戏 目录\\原神", "quote\"inside", "trailing\\", "trailing space\\",
        "\\\\server\\share\\", "a\\\\\"b", "& | > < ^ %PATH%", "tab\there", "\"", "\\\"",
    };

    [Theory]
    [MemberData(nameof(TrickyArguments))]
    public void ArgumentsRoundTripThroughTheWindowsParser(string argument)
    {
        var line = CommandLine.Build(@"C:\Engines\fake.exe", ["--x", argument, "end"]);
        Assert.Equal(new[] { @"C:\Engines\fake.exe", "--x", argument, "end" }, ParseLikeMsvcrt(line));
    }

    [Fact]
    public void QuotesInProgramPathAreRejected() =>
        Assert.Throws<ArgumentException>(() => CommandLine.Build("C:\\a\"b.exe", []));

    [Fact]
    public void NulInArgumentIsRejected() =>
        Assert.Throws<ArgumentException>(() => CommandLine.Build(@"C:\a.exe", ["x\0y"]));

    [Fact]
    public void EnvironmentBlockIsSortedAndTerminated()
    {
        var block = new string(CommandLine.BuildEnvironmentBlock(new Dictionary<string, string> { ["ZZ_VIVIAN_TEST"] = "1" }));
        Assert.EndsWith("\0\0", block);
        Assert.Contains("ZZ_VIVIAN_TEST=1\0", block);
        Assert.Throws<ArgumentException>(() => CommandLine.BuildEnvironmentBlock(new Dictionary<string, string> { ["A=B"] = "x" }));
    }

    /// <summary>Reference implementation of the documented MSVC argv parsing rules (program name is never escaped).</summary>
    private static List<string> ParseLikeMsvcrt(string line)
    {
        var result = new List<string>();
        var i = 0;
        // Program name: quoted, no escape processing.
        if (line[0] == '"') { var end = line.IndexOf('"', 1); result.Add(line[1..end]); i = end + 1; }
        while (i < line.Length)
        {
            while (i < line.Length && line[i] is ' ' or '\t') i++;
            if (i >= line.Length) break;
            var current = new StringBuilder();
            var inQuotes = false;
            while (i < line.Length && (inQuotes || line[i] is not (' ' or '\t')))
            {
                var backslashes = 0;
                while (i < line.Length && line[i] == '\\') { backslashes++; i++; }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) { current.Append('"'); i++; }
                    else if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i += 2; }
                    else { inQuotes = !inQuotes; i++; }
                }
                else
                {
                    current.Append('\\', backslashes);
                    if (i < line.Length && (inQuotes || line[i] is not (' ' or '\t'))) { current.Append(line[i]); i++; }
                }
            }
            result.Add(current.ToString());
        }
        return result;
    }
}

public sealed class FakeEngineProtocolTests
{
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    [Fact]
    public void CompletionEvidenceForThisRunIsRecognised()
    {
        var parsed = FakeEngineProtocol.Parse(Run, $"VIVIAN-FAKE|{Run}|evidence|completion", At);
        Assert.Equal(EvidenceKind.CompletionReported, parsed.Evidence!.Kind);
        Assert.Equal(Run, parsed.Evidence.RunId);
    }

    [Fact]
    public void EvidenceFromAnotherRunIsDowngradedToAWarning()
    {
        var parsed = FakeEngineProtocol.Parse(Run, $"VIVIAN-FAKE|{Guid.NewGuid()}|evidence|completion", At);
        Assert.Null(parsed.Evidence);
        Assert.Equal(RunEventKind.Warning, parsed.Event.Kind);
        Assert.Equal(Run, parsed.Event.RunId);
    }

    [Theory]
    [InlineData("ERROR: 任务失败")]
    [InlineData("成功")]
    [InlineData("Traceback (most recent call last):")]
    public void FreeTextNeverBecomesEvidence(string line)
    {
        var parsed = FakeEngineProtocol.Parse(Run, line, At);
        Assert.Null(parsed.Evidence);
        Assert.Equal(RunEventKind.Output, parsed.Event.Kind);
    }

    [Theory]
    [InlineData("VIVIAN-FAKE|not-a-guid|evidence|completion")]
    [InlineData("VIVIAN-FAKE|broken")]
    public void MalformedStructuredLinesAreWarnings(string line)
    {
        var parsed = FakeEngineProtocol.Parse(Run, line, At);
        Assert.Null(parsed.Evidence);
        Assert.Equal(RunEventKind.Warning, parsed.Event.Kind);
    }

    [Fact]
    public void UnknownEvidenceKindIsNotEvidence()
    {
        var parsed = FakeEngineProtocol.Parse(Run, $"VIVIAN-FAKE|{Run}|evidence|probably-done", At);
        Assert.Null(parsed.Evidence);
    }

    [Fact]
    public async Task MissingExecutableSkipsGameWithoutBlockingQueue()
    {
        var adapter = new FakeEngineAdapter(GameId.Genshin, Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".exe"), "success");
        var result = await adapter.PreflightAsync(new RunContext(Run, Guid.NewGuid(), GameId.Genshin, At), default);
        Assert.False(result.CanStart);
        Assert.False(result.BlocksQueue);
        Assert.Equal(ReasonCodes.NotConfigured, result.ReasonCode);
    }
}
