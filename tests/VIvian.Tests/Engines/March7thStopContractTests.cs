using VIvian.Infrastructure.Engines.Cli;
using Xunit;

namespace VIvian.Tests.Engines;

public class March7thStopContractTests
{
    [Fact]
    public void RequiresExactRunIdAndExplicitAcknowledgement()
    {
        const string id = "20261031-123456-ab12";
        var receipt = $$"""{"run_id":"{{id}}","action":"stop","acknowledged":true,"state":"stopping","status":"running"}""";
        Assert.True(March7thStopContract.Acknowledged(receipt, id));
        Assert.False(March7thStopContract.Acknowledged(receipt, "20261031-123456-ffff"));
        Assert.False(March7thStopContract.Acknowledged(receipt.Replace("true", "false"), id));
        Assert.False(March7thStopContract.Acknowledged(receipt.Replace("\"stop\"", "\"pause\""), id));
        Assert.False(March7thStopContract.Acknowledged("not json", id));
    }
}
