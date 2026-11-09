using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VIvian.Infrastructure.Assistant;

public sealed record AssistantEndpoint(string BaseUrl, string Model, string ApiKey)
{
    internal Uri? ChatUri
    {
        get
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                return null;
            var address = uri.AbsoluteUri.TrimEnd('/');
            return address.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? new Uri(address) : new Uri(address + "/chat/completions");
        }
    }

    public bool IsComplete => ChatUri is not null && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey) && ApiKey.All(c => c is > ' ' and <= '~');

    // A record's default string representation would include the key.
    public override string ToString() => nameof(AssistantEndpoint);
}

public sealed class AssistantException(string message) : Exception(message);

public sealed class ChatCompletionsClient(HttpClient http)
{
    public async Task TestConnectionAsync(AssistantEndpoint endpoint, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var message = await CompleteAsync(endpoint,
                [new JsonObject { ["role"] = "user", ["content"] = "Reply briefly with OK." }], null, deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(StringValue(message["content"])))
                throw new AssistantException("模型未返回有效文字，请确认所填模型支持聊天。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException("连接测试超时，请检查网络或接口地址。");
        }
    }

    public async Task<JsonObject> CompleteAsync(AssistantEndpoint endpoint, JsonArray messages, JsonArray? tools, CancellationToken ct)
    {
        if (!endpoint.IsComplete)
            throw new AssistantException("请检查 AI 接口地址、模型名和密钥。远程接口需要 HTTPS。");
        var body = new JsonObject { ["model"] = endpoint.Model, ["messages"] = messages.DeepClone(), ["stream"] = false };
        if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.ChatUri)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AssistantException(StatusMessage(response.StatusCode));
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (JsonNode.Parse(text) is JsonObject root && root["choices"] is JsonArray { Count: > 0 } choices
                && choices[0] is JsonObject choice && choice["message"] is JsonObject message
                && StringValue(message["role"]) == "assistant")
                return message;
            throw new AssistantException("AI 接口未返回兼容的聊天响应，请核对地址和模型。");
        }
        catch (JsonException) { throw new AssistantException("AI 接口返回了无效的 JSON。"); }
        catch (HttpRequestException) { throw new AssistantException("AI 接口请求失败，请检查网络、证书或代理。"); }
    }

    internal static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string StatusMessage(HttpStatusCode status)
    {
        var detail = (int)status switch
        {
            401 or 403 => "鉴权失败，请核对密钥及模型权限。",
            400 or 422 => "请求被拒绝，请核对模型及聊天、工具调用兼容性。",
            404 => "接口或模型未找到，请核对地址及模型名。",
            429 => "请求受限，请检查额度或稍后重试。",
            >= 300 and < 400 => "接口要求跳转，请直接填写最终地址。",
            >= 500 => "服务暂不可用，请稍后重试。",
            _ => "请求未成功，请核对接口设置。",
        };
        return $"AI 接口返回 {(int)status}。{detail}";
    }
}
