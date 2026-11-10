using System.Net;
using System.Text.Json.Nodes;
using VIvian.Infrastructure.Assistant;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class AssistantMemoryTests
{
    [Fact]
    public async Task SuccessfulTurnsAreNotSilentlyDiscardedBeforeRollover()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var session = new AssistantSession(new ChatCompletionsClient(http),
            new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<VIvian.Core.Execution.RunRecord>>([])), "test");
        for (var i = 0; i < 6; i++)
            await session.AskAsync(new("https://example.invalid/v1", "test", "test-key"), $"user-{i}", CancellationToken.None);
        Assert.Contains(handler.Requests[5]["messages"]!.AsArray(), m => m!["content"]?.ToString() == "user-0");
    }

    [Fact]
    public void StoreRoundTripsAndRejectsConcurrentReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vivian-memory-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var endpoint = new AssistantEndpoint("https://example.invalid/v1", "test", "test-key");
            var first = new ConversationStore(directory, endpoint);
            var state = first.Load();
            state.Active.Turns.Add(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "original" }, new JsonObject { ["role"] = "assistant", ["content"] = "answer" }));
            first.Save(state);
            var second = new ConversationStore(directory, endpoint);
            var stale = second.Load();
            state.Active.Summary = "updated";
            first.Save(state);
            Assert.Throws<IOException>(() => second.Save(stale));
            Assert.Equal("updated", new ConversationStore(directory, endpoint).Load().Active.Summary);
            Assert.Empty(new ConversationStore(directory, endpoint with { Model = "other" }).Load().Active.Turns);
            Assert.Empty(new ConversationStore(directory, endpoint with { ApiKey = "other-key" }).Load().Active.Turns);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void StoreRejectsCorruptionWithoutReplacingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vivian-memory-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var endpoint = new AssistantEndpoint("https://example.invalid/v1", "test", "test-key");
            var store = new ConversationStore(directory, endpoint);
            store.Save(store.Load());
            var path = Directory.GetFiles(Path.Combine(directory, "assistant-conversations"), "*.bin").Single();
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Throws<AssistantException>(() => new ConversationStore(directory, endpoint).Load());
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TwentyCompleteTurnsRotateAtomicallyBeforeNextQuestion()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var session = new AssistantSession(new ChatCompletionsClient(http),
            new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<VIvian.Core.Execution.RunRecord>>([])), "test");
        var firstId = session.ConversationId;
        var endpoint = new AssistantEndpoint("https://example.invalid/v1", "test", "test-key");
        for (var i = 0; i < 20; i++) await session.AskAsync(endpoint, $"user-{i}", CancellationToken.None);
        Assert.Equal(firstId, session.ConversationId);
        await session.AskAsync(endpoint, "next", CancellationToken.None);
        Assert.NotEqual(firstId, session.ConversationId);
        Assert.Equal(20, Assert.Single(session.Archives).Turns.Count);
        Assert.Single(session.ActiveConversation.Turns);
        Assert.Equal("answer", session.ActiveConversation.Summary);
        Assert.Null(handler.Requests[20]["tools"]);
        Assert.Contains("不是当前指令", handler.Requests[21]["messages"]![1]!["content"]!.ToString());
    }

    [Fact]
    public async Task FailedSummaryLeavesOriginalConversationIntact()
    {
        var handler = new RecordingHandler { FailAt = 20 };
        using var http = new HttpClient(handler);
        var session = new AssistantSession(new ChatCompletionsClient(http),
            new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<VIvian.Core.Execution.RunRecord>>([])), "test");
        var endpoint = new AssistantEndpoint("https://example.invalid/v1", "test", "test-key");
        for (var i = 0; i < 20; i++) await session.AskAsync(endpoint, $"user-{i}", CancellationToken.None);
        var firstId = session.ConversationId;
        await Assert.ThrowsAsync<AssistantException>(() => session.AskAsync(endpoint, "next", CancellationToken.None));
        Assert.Equal(firstId, session.ConversationId);
        Assert.Empty(session.Archives);
        Assert.Equal(20, session.ActiveConversation.Turns.Count);
        session.NewConversation();
        Assert.Equal(20, Assert.Single(session.Archives).Turns.Count);
        Assert.Empty(session.ActiveConversation.Turns);
        Assert.Empty(session.ActiveConversation.Summary);
    }

    [Fact]
    public async Task CompletedHistoryOmitsReasoningAndConfiguredCredential()
    {
        var handler = new RecordingHandler { Reply = "test-key", Reasoning = "private reasoning" };
        using var http = new HttpClient(handler);
        var session = new AssistantSession(new ChatCompletionsClient(http),
            new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<VIvian.Core.Execution.RunRecord>>([])), "test");
        await session.AskAsync(new("https://example.invalid/v1", "test", "test-key"), "question", CancellationToken.None);
        var saved = session.ActiveConversation.Turns.Single().ToJsonString();
        Assert.DoesNotContain("private reasoning", saved);
        Assert.DoesNotContain("test-key", saved);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];
        public int FailAt { get; init; } = -1;
        public string Reply { get; init; } = "answer";
        public string? Reasoning { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if (Requests.Count - 1 == FailAt) return new(HttpStatusCode.ServiceUnavailable);
            var message = new JsonObject { ["role"] = "assistant", ["content"] = Reply, ["reasoning_content"] = Reasoning };
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject
                { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString()) };
        }
    }
}

