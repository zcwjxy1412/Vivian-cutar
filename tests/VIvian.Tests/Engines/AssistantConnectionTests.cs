using System.Net;
using System.Text.Json.Nodes;
using VIvian.Infrastructure.Assistant;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class AssistantConnectionTests
{
    private static readonly AssistantEndpoint Endpoint = new("https://example.invalid/v1/", "test-model", "test-key");

    [Theory]
    [InlineData("https://example.invalid/v1", true)]
    [InlineData("http://127.0.0.1:8000/v1", true)]
    [InlineData("http://example.invalid/v1", false)]
    [InlineData("https://name:password@example.invalid/v1", false)]
    [InlineData("https://example.invalid/v1?key=example", false)]
    [InlineData("https://example.invalid/v1#fragment", false)]
    public void OnlySupportedAddressesAreComplete(string url, bool expected) =>
        Assert.Equal(expected, (Endpoint with { BaseUrl = url }).IsComplete);

    [Fact]
    public async Task TestConnectionUsesConfiguredAddressModelAndBearer()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://example.invalid/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("test-model", body["model"]!.GetValue<string>());
            Assert.Null(body["tools"]);
            return new(HttpStatusCode.OK) { Content = new StringContent(
                """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""") };
        }));
        await new ChatCompletionsClient(http).TestConnectionAsync(Endpoint, CancellationToken.None);
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":{}}")]
    [InlineData("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":12}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"role\":\"user\",\"content\":\"OK\"}}]}")]
    [InlineData("not JSON")]
    public async Task TestConnectionRejectsInvalidSuccessPayload(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        await Assert.ThrowsAsync<AssistantException>(
            () => new ChatCompletionsClient(http).TestConnectionAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReachesTheHttpRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChatCompletionsClient(http).TestConnectionAsync(Endpoint, cancellation.Token));
    }

    [Fact]
    public async Task ToolProbeRequiresARealCallAndEchoedResult()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var message = ++requests switch
            {
                1 => JsonNode.Parse("""
                    {"role":"assistant","content":null,"tool_calls":[{"id":"probe-1","type":"function",
                    "function":{"name":"vivian_probe","arguments":"{}"}}]}
                    """)!.AsObject(),
                2 => new JsonObject { ["role"] = "assistant", ["content"] = body["messages"]!.AsArray().Last()!["content"]!.GetValue<string>() },
                _ => throw new InvalidOperationException("Unexpected probe request."),
            };
            if (requests == 2)
                Assert.Equal("probe-1", body["messages"]!.AsArray().Last()!["tool_call_id"]!.GetValue<string>());
            var result = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) };
            return new(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString()) };
        }));
        await new ChatCompletionsClient(http).TestToolRoundTripAsync(Endpoint, CancellationToken.None);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ModelReturningOnlyTextFailsTheToolProbe()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"OK"}}]}"""),
        })));
        await Assert.ThrowsAsync<AssistantException>(() =>
            new ChatCompletionsClient(http).TestToolRoundTripAsync(Endpoint, CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
