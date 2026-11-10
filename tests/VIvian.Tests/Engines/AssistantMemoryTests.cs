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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"answer\"}}]}") };
        }
    }
}
