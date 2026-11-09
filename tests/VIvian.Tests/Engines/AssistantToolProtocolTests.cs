using System.Net;
using System.Text.Json.Nodes;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Assistant;
using VIvian.Infrastructure.Engines;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class AssistantToolProtocolTests
{
    private static readonly AssistantEndpoint Endpoint = new("https://example.invalid/v1", "test-model", "test-key");
    private static readonly string Answer = """{"role":"assistant","content":"完成"}""";

    [Theory]
    [InlineData("unknown", "{}")]
    [InlineData("list_games", "[]")]
    [InlineData("list_games", "not JSON")]
    [InlineData("list_games", "{\"extra\":1}")]
    [InlineData("get_run_status", "{\"limit\":\"1\"}")]
    public async Task RejectedToolArgumentsAreReturnedWithTheirCallId(string name, string arguments)
    {
        var handler = new Handler(Turn(Call("call_1", name, arguments)), Answer);
        using var http = new HttpClient(handler);
        var session = Session(http);
        Assert.Equal("完成", await session.AskAsync(Endpoint, "查询", CancellationToken.None));
        var response = handler.Requests[1]["messages"]!.AsArray().Last()!;
        Assert.Equal("call_1", response["tool_call_id"]!.GetValue<string>());
        Assert.Contains("error", response["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task DuplicateIdsRejectTheTurnAndTheNextTurnHasCleanHistory()
    {
        var handler = new Handler(Turn(Call("duplicate", "list_games", "{}"), Call("duplicate", "list_games", "{}")), Answer);
        using var http = new HttpClient(handler);
        var session = Session(http);
        await Assert.ThrowsAsync<AssistantException>(() => session.AskAsync(Endpoint, "失败轮次", CancellationToken.None));
        Assert.Equal("完成", await session.AskAsync(Endpoint, "重试轮次", CancellationToken.None));
        Assert.DoesNotContain(handler.Requests[1]["messages"]!.AsArray(), m => m!["content"]?.GetValue<string>() == "失败轮次");
        Assert.Equal(2, handler.Requests[1]["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task ToolTimeoutDoesNotPreventTheFinalReplyOrTheNextTurn()
    {
        var handler = new Handler(Turn(Call("slow", "get_run_status", "{}")), Answer, Answer);
        using var http = new HttpClient(handler);
        var session = Session(http, async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        });
        Assert.Equal("完成", await session.AskAsync(Endpoint, "查询", CancellationToken.None));
        Assert.Contains("超时", handler.Requests[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>());
        Assert.Equal("完成", await session.AskAsync(Endpoint, "再问", CancellationToken.None));
    }

    private static AssistantSession Session(HttpClient http, Func<CancellationToken, Task<IReadOnlyList<RunRecord>>>? history = null) =>
        new(new ChatCompletionsClient(http),
            new AssistantTools(new VersionedConfigurationControl(), _ => "", () => [], history ?? (_ => Task.FromResult<IReadOnlyList<RunRecord>>([]))),
            "芙宁娜", toolTimeout: TimeSpan.FromMilliseconds(100));

    private static JsonObject Call(string id, string name, string arguments) => new()
    {
        ["id"] = id, ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    };

    private static string Turn(params JsonObject[] calls) =>
        new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(calls) }.ToJsonString();

    private sealed class Handler(params string[] messages) : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":" + messages[Requests.Count - 1] + "}]}") };
        }
    }
}
