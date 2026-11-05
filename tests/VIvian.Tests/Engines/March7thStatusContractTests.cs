using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

public class March7thStatusContractTests
{
    private const string Ready = """
        {"schema":1,"first_run_completed":true,"game_path_exists":true,"cloud_game_enable":false,"active_run":null,"elevation":"uac"}
        """;

    [Fact]
    public void ValidSnapshotPermitsOnlyNextValidationStep()
    {
        var result = March7thStatusContract.Parse(Ready);
        Assert.True(result.ReadyForUacValidation);
        Assert.Contains("仍需桌面验证", result.Detail);
        Assert.True(March7thStatusContract.Parse(Ready.Replace(",\"elevation\":\"uac\"", "")).ReadyForUacValidation);
    }

    [Theory]
    [InlineData("\"first_run_completed\":true", "\"first_run_completed\":false")]
    [InlineData("\"active_run\":null", "\"active_run\":{\"run_id\":\"busy\"}")]
    [InlineData("\"game_path_exists\":true", "\"game_path_exists\":false")]
    [InlineData("\"schema\":1", "\"schema\":2")]
    public void RefusesUnsafeOrUnknownSnapshot(string from, string to)
        => Assert.False(March7thStatusContract.Parse(Ready.Replace(from, to)).ReadyForUacValidation);

    [Fact]
    public void ReportsDistinctBlockingFields()
    {
        Assert.Equal(March7thStatusBlock.FirstRunNotConfirmed, March7thStatusContract.Parse(Ready.Replace("\"first_run_completed\":true", "\"first_run_completed\":false")).Block);
        Assert.Equal(March7thStatusBlock.GamePathMissing, March7thStatusContract.Parse(Ready.Replace("\"game_path_exists\":true", "\"game_path_exists\":false")).Block);
        Assert.Equal(March7thStatusBlock.ActiveRun, March7thStatusContract.Parse(Ready.Replace("\"active_run\":null", "\"active_run\":{}")).Block);
        Assert.Equal(March7thStatusBlock.Protocol, March7thStatusContract.Parse("{}").Block);
    }

    [Fact]
    public void RefusesMalformedSnapshot()
    {
        Assert.False(March7thStatusContract.Parse("not json").ReadyForUacValidation);
        Assert.False(March7thStatusContract.Parse("{}").ReadyForUacValidation);
    }
}
