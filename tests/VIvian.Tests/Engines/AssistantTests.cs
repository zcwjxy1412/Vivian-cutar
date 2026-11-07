using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Assistant;
using VIvian.Infrastructure.Engines;
using Xunit;

namespace VIvian.Tests.Engines;

/// <summary>The assistant reads through VIvian's tools and can only prepare proposals; the user applies them.</summary>
public sealed class AssistantTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-ai-" + Guid.NewGuid().ToString("N"));
    private readonly VersionedConfigurationControl _control = new();
    private string Config => Path.Combine(_root, "config.yaml");
    private static readonly AssistantEndpoint Endpoint = new("https://example.invalid/v1", "test-model", "sk-secret-123");

    public AssistantTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        File.WriteAllText(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.10.31\n");
        File.WriteAllText(Path.Combine(_root, "assets", "config", "instance_names.json"),
            "{\"饰品提取\": {\"无\": \"跳过\", \"永恒笑剧\": \"\"}, \"拟造花萼（金）\": {\"无\": \"跳过\", \"回忆之蕾\": \"角色经验\"}}");
        File.WriteAllText(Config, "power_enable: true\r\ninstance_type: 拟造花萼（金）\r\ninstance_names:\r\n  拟造花萼（金）: 回忆之蕾\r\n  饰品提取: 永恒笑剧\r\n");
    }
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private AssistantTools Tools() => new(_control, _ => _root,
        () => [new EngineStatusView(GameId.StarRail, "崩坏：星穹铁道", "March7thAssistant", "v2026.10.31", "已核对", true)],
        () => Task.FromResult<IReadOnlyList<RunRecord>>([]));

    [Fact]
    public async Task ProposeChangesValidatesButNeverWritesUntilTheUserConfirms()
    {
        var tools = Tools();
        var read = JsonNode.Parse(await tools.ExecuteAsync("read_configuration", "{\"game\":\"starrail\"}"))!.AsArray();
        Assert.Contains(read, n => n!["Path"]!.GetValue<string>() == "instance_type" && n["value"]!.GetValue<string>() == "拟造花萼（金）");
        var before = File.ReadAllText(Config);
        var result = JsonNode.Parse(await tools.ExecuteAsync("propose_changes", "{\"changes\":{\"instance_type\":\"饰品提取\"}}"))!;
        Assert.True(result["valid"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllText(Config)); // proposal only
        var id = Assert.Single(tools.NewProposals).Id;
        tools.ApplyConfirmed(id, Path.Combine(_root, "audit"), engineRunning: false);
        Assert.Contains("instance_type: 饰品提取", File.ReadAllText(Config));
        Assert.Throws<InvalidOperationException>(() => tools.ApplyConfirmed(id, Path.Combine(_root, "audit"), false)); // single use
    }

    [Fact]
    public async Task InvalidOrUnknownRequestsComeBackAsErrorsNotWrites()
    {
        var tools = Tools();
        var bad = JsonNode.Parse(await tools.ExecuteAsync("propose_changes", "{\"changes\":{\"instance_type\":\"随便写的\"}}"))!;
        Assert.False(bad["valid"]!.GetValue<bool>());
        Assert.Empty(tools.NewProposals);
        Assert.Contains("error", await tools.ExecuteAsync("delete_everything", "{}"));
        Assert.Contains("error", await tools.ExecuteAsync("propose_changes", "{\"changes\":{\"not_a_field\":\"1\"}}"));
        Assert.Contains("error", await tools.ExecuteAsync("list_tasks", "not json"));
    }

    [Fact]
    public async Task SessionRunsTheToolLoopAndReturnsProposalCards()
    {
        var handler = new ScriptedHandler(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"propose_changes","arguments":"{\"changes\":{\"instance_type\":\"饰品提取\"}}"}}]}}]}""",
            """{"choices":[{"message":{"role":"assistant","content":"已为你准备好修改，请点确认。"}}]}""");
        var session = new AssistantSession(new ChatCompletionsClient(new HttpClient(handler)), Tools(), "小星");
        var reply = await session.AskAsync(Endpoint, "清体力改成饰品提取", CancellationToken.None);
        Assert.Equal("已为你准备好修改，请点确认。", reply);
        Assert.Single(session.LastProposals);
        Assert.Equal("Bearer sk-secret-123", handler.Authorizations[0]);
        var second = JsonNode.Parse(handler.Bodies[1])!;
        Assert.Contains(second["messages"]!.AsArray(), m => m!["role"]!.GetValue<string>() == "tool");
        Assert.Contains("拟造花萼（金）", File.ReadAllText(Config)); // still unchanged
    }

    [Fact]
    public async Task ApiErrorsNeverEchoTheKey()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Unauthorized, "invalid key sk-secret-123");
        var client = new ChatCompletionsClient(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<AssistantException>(() => client.CompleteAsync(Endpoint, [], null, CancellationToken.None));
        Assert.DoesNotContain("sk-secret-123", error.Message);
        Assert.Contains("401", error.Message);
        Assert.False(new AssistantEndpoint("ftp://x", "m", "k").IsComplete);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _replies = new();
        public List<string> Bodies { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public ScriptedHandler(params string[] replies) { foreach (var r in replies) _replies.Enqueue((HttpStatusCode.OK, r)); }
        public ScriptedHandler(HttpStatusCode status, string body) => _replies.Enqueue((status, body));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            var (status, body) = _replies.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
