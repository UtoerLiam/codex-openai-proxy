using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Serilog;

namespace CodexOpenAIProxy;

/// <summary>
/// 代理核心服务：
/// 1) 将下游 OpenAI-compatible 请求改写为上游 Codex /v1/responses。
/// 2) 负责 non-stream 与 stream(SSE) 两种转发模式。
/// 3) 负责 requestId 透传、超时控制、取消联动与错误包装。
/// </summary>
public sealed class ProxyService(HttpClient httpClient, ProxyServiceOptions options)
{
    /// <summary>
    /// 输入已经是 /v1/responses 时，仅做模型映射，其他字段尽量保持不变（透传友好）。
    /// </summary>
    public JsonObject BuildResponsesRequest(JsonObject downstream, ModelMapper mapper)
    {
        var rewritten = (JsonObject)downstream.DeepClone();
        var originalModel = downstream["model"]?.GetValue<string>();
        rewritten["model"] = mapper.Map(originalModel);
        return rewritten;
    }

    /// <summary>
    /// 将 /v1/chat/completions 请求桥接为 /v1/responses 请求。
    /// 关键转换：messages -> input。
    /// 其余字段（stream/tools/temperature/max_output_tokens 等）尽量原样复制。
    /// </summary>
    public JsonObject BuildResponsesRequestFromChat(JsonObject downstreamChat, ModelMapper mapper)
    {
        var rewritten = new JsonObject();

        foreach (var kv in downstreamChat)
        {
            // messages 需要单独改写到 input，因此先跳过。
            if (string.Equals(kv.Key, "messages", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rewritten[kv.Key] = kv.Value?.DeepClone();
        }

        var originalModel = downstreamChat["model"]?.GetValue<string>();
        rewritten["model"] = mapper.Map(originalModel);

        if (downstreamChat["messages"] is JsonArray messages)
        {
            rewritten["input"] = messages.DeepClone();
        }

        return rewritten;
    }

    /// <summary>
    /// 将改写后的请求转发到上游 /v1/responses。
    /// </summary>
    public async Task<IResult> ForwardResponsesAsync(HttpContext context, JsonObject upstreamPayload, CancellationToken requestAborted)
    {
        var requestId = context.Items["RequestId"]?.ToString() ?? Guid.NewGuid().ToString("N")[..12];
        var stream = upstreamPayload["stream"]?.GetValue<bool>() ?? false;
        var upstreamUrl = new Uri(new Uri(options.UpstreamBaseUrl), "/v1/responses");

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new StringContent(upstreamPayload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        // 透传 requestId 便于上下游日志关联。
        upstreamRequest.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.UpstreamBearerToken);
        upstreamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));

        // 超时策略：
        // - 非流式：限制在 NonStreamingTimeout（默认 120s）
        // - 流式：不施加固定超时，依赖客户端取消/上游结束
        using var timeoutCts = stream ? null : new CancellationTokenSource(options.NonStreamingTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            requestAborted,
            timeoutCts?.Token ?? CancellationToken.None);

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await httpClient.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            // 下游（Cursor）主动取消时，直接结束本次请求。
            Log.Warning("[{RequestId}] downstream request aborted by client", requestId);
            return Results.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{RequestId}] upstream request failed", requestId);
            return Results.Json(OpenAiError("upstream_connection_error", "Failed to reach upstream Codex API."), statusCode: 502);
        }

        await using var _ = upstreamResponse;
        context.Response.StatusCode = (int)upstreamResponse.StatusCode;

        var contentType = upstreamResponse.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            context.Response.ContentType = contentType;
        }

        if (!stream)
        {
            // 非流式：完整读取后返回。
            // 对上游错误码（4xx/5xx）保持透传；若 body 为空则补一个可读错误。
            var body = await upstreamResponse.Content.ReadAsStringAsync(requestAborted);
            if ((int)upstreamResponse.StatusCode >= 400 && string.IsNullOrWhiteSpace(body))
            {
                return Results.Json(OpenAiError("upstream_error", $"Upstream returned HTTP {(int)upstreamResponse.StatusCode}."),
                    statusCode: (int)upstreamResponse.StatusCode);
            }

            return Results.Text(body, contentType ?? "application/json", Encoding.UTF8, (int)upstreamResponse.StatusCode);
        }

        // 流式：按字节块持续转发，避免“上游读完再一次性回写”造成的流式失效。
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(requestAborted);
        var buffer = new byte[8 * 1024];

        while (true)
        {
            var read = await upstreamStream.ReadAsync(buffer, requestAborted);
            if (read == 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), requestAborted);
            await context.Response.Body.FlushAsync(requestAborted);
        }

        return Results.Empty;
    }

    private static object OpenAiError(string type, string message) => new
    {
        error = new
        {
            type,
            message
        }
    };
}

/// <summary>
/// 代理服务配置。
/// </summary>
public sealed class ProxyServiceOptions
{
    public required string UpstreamBaseUrl { get; init; }
    public required string UpstreamBearerToken { get; init; }
    public TimeSpan NonStreamingTimeout { get; init; } = TimeSpan.FromSeconds(120);
}
