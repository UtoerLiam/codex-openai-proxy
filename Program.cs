using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CodexOpenAIProxy;
using Serilog;

// =============================
// 启动阶段：读取运行配置
// =============================
// BIND/PORT 控制监听地址与端口，默认仅监听本机 127.0.0.1:8181。
var bind = Environment.GetEnvironmentVariable("BIND") ?? "127.0.0.1";
var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var parsedPort) ? parsedPort : 8181;

// 上游 Codex API 基础地址，可通过环境变量覆盖。
var upstreamBaseUrl = Environment.GetEnvironmentVariable("CODEX_UPSTREAM_BASE_URL") ?? "https://api.openai.com";

// 默认 auth.json 路径；支持 CODEX_AUTH_PATH 自定义。
var authPath = Environment.GetEnvironmentVariable("CODEX_AUTH_PATH") ?? AuthLoader.GetDefaultAuthPath();

// 若直接提供 CODEX_UPSTREAM_BEARER，则优先使用该令牌，不再读取 auth.json。
var bearerOverride = Environment.GetEnvironmentVariable("CODEX_UPSTREAM_BEARER");

// 日志目录：Serilog 文件输出会落在 logs/app-YYYYMMDD.log。
Directory.CreateDirectory("logs");

// =============================
// 日志初始化：控制台 + 文件
// =============================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/app-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true)
    .CreateLogger();

try
{
    // =============================
    // 认证加载：环境变量优先，其次 auth.json
    // =============================
    string upstreamToken;
    if (!string.IsNullOrWhiteSpace(bearerOverride))
    {
        upstreamToken = bearerOverride;
        Log.Information("Using upstream bearer token from CODEX_UPSTREAM_BEARER.");
    }
    else
    {
        var authResult = await AuthLoader.LoadTokenAsync(authPath);
        upstreamToken = authResult.Token;

        // 仅记录“命中的条目路径”，不打印 token 本文。
        Log.Information("Loaded Codex token from {AuthPath}. Selected entry: {EntryName}", authPath, authResult.EntryName);
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // 注入代理配置：非流式请求默认 120 秒超时；流式请求不做固定超时限制。
    builder.Services.AddSingleton(new ProxyServiceOptions
    {
        UpstreamBaseUrl = upstreamBaseUrl,
        UpstreamBearerToken = upstreamToken,
        NonStreamingTimeout = TimeSpan.FromSeconds(120)
    });

    builder.Services.AddSingleton<ModelMapper>();

    // HttpClient 使用无限超时，真正超时逻辑由 ProxyService 按 stream/non-stream 区分控制。
    builder.Services.AddHttpClient<ProxyService>()
        .ConfigureHttpClient(client => { client.Timeout = Timeout.InfiniteTimeSpan; });

    var app = builder.Build();

    // =============================
    // 请求日志中间件
    // =============================
    // 作用：
    // 1) 为每个请求分配 requestId
    // 2) 记录请求行、headers 摘要、可选 body 摘要
    // 3) 记录响应状态码与耗时
    // 4) 异常时记录堆栈
    app.Use(async (context, next) =>
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        context.Items["RequestId"] = requestId;

        var sw = Stopwatch.StartNew();
        var method = context.Request.Method;
        var path = context.Request.Path.ToString();

        var requestBodyPreview = await ReadBodyPreviewAsync(context.Request, maxBytes: 64 * 1024);
        var headerSummary = BuildHeaderSummary(context.Request.Headers);

        Log.Information("[{RequestId}] --> {Method} {Path} headers={Headers}", requestId, method, path, headerSummary);

        // 仅在 Development 输出 body 摘要，避免生产环境日志过大或泄露输入。
        if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(requestBodyPreview))
        {
            Log.Debug("[{RequestId}] request_body={Body}", requestId, requestBodyPreview);
        }

        try
        {
            await next();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{RequestId}] Unhandled exception", requestId);
            throw;
        }
        finally
        {
            sw.Stop();
            Log.Information("[{RequestId}] {Method} {Path} {StatusCode} {ElapsedMs}ms",
                requestId,
                method,
                path,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds);
        }
    });

    // 健康检查，便于探活。
    app.MapGet("/health", () => Results.Json(new { status = "ok" }));

    // OpenAI-compatible: 模型列表接口。
    // 同时兼容 /models 与 /v1/models 两种路径，降低下游接入成本。
    var modelsHandler = (ModelMapper mapper) => Results.Json(new
    {
        @object = "list",
        data = mapper.GetExternalModels().Select(model => new
        {
            id = model,
            @object = "model",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            owned_by = "codex-proxy"
        })
    });
    app.MapGet("/models", modelsHandler);
    app.MapGet("/v1/models", modelsHandler);

    // OpenAI-compatible: /responses 与 /v1/responses
    // 策略：尽量少做强校验，只在 JSON 非法时返回 400；其他字段尽量透传。
    var responsesHandler = async (HttpContext context, ProxyService proxyService, ModelMapper mapper) =>
    {
        var json = await ReadJsonBodyAsync(context);
        if (json is null)
        {
            return Results.Json(OpenAiError("invalid_request_error", "Invalid JSON payload."), statusCode: 400);
        }

        var rewritten = proxyService.BuildResponsesRequest(json, mapper);
        return await proxyService.ForwardResponsesAsync(context, rewritten, context.RequestAborted);
    };
    app.MapPost("/responses", responsesHandler);
    app.MapPost("/v1/responses", responsesHandler);

    // OpenAI-compatible: /chat/completions 与 /v1/chat/completions
    // 桥接策略：将 messages 转换到 responses.input，然后统一走上游 /v1/responses。
    var chatCompletionsHandler = async (HttpContext context, ProxyService proxyService, ModelMapper mapper) =>
    {
        var json = await ReadJsonBodyAsync(context);
        if (json is null)
        {
            return Results.Json(OpenAiError("invalid_request_error", "Invalid JSON payload."), statusCode: 400);
        }

        var rewritten = proxyService.BuildResponsesRequestFromChat(json, mapper);
        return await proxyService.ForwardResponsesAsync(context, rewritten, context.RequestAborted);
    };
    app.MapPost("/chat/completions", chatCompletionsHandler);
    app.MapPost("/v1/chat/completions", chatCompletionsHandler);

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Log.Information("Codex OpenAI proxy listening on http://{Bind}:{Port}, upstream={Upstream}", bind, port, upstreamBaseUrl);
    });

    await app.RunAsync($"http://{bind}:{port}");
    return;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Failed to start proxy.");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// 从请求体读取 JSON 对象。
/// - 返回 null：JSON 非法或非对象。
/// - 注意：本层不做严格 schema 校验，尽量兼容 Cursor 可能附带的扩展字段。
/// </summary>
static async Task<JsonObject?> ReadJsonBodyAsync(HttpContext context)
{
    try
    {
        var node = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        return node as JsonObject;
    }
    catch
    {
        return null;
    }
}

/// <summary>
/// 读取请求体摘要用于日志排查。
/// - 最大读取 maxBytes，超出则追加 "...(truncated)"。
/// - 读取后会重置流位置，避免影响后续业务读取请求体。
/// </summary>
static async Task<string> ReadBodyPreviewAsync(HttpRequest request, int maxBytes)
{
    if (request.Body.CanSeek)
    {
        request.Body.Position = 0;
    }
    else
    {
        request.EnableBuffering();
    }

    var buffer = new byte[maxBytes + 1];
    var read = await request.Body.ReadAsync(buffer.AsMemory(0, maxBytes + 1));
    request.Body.Position = 0;

    var truncated = read > maxBytes;
    var body = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, maxBytes));
    return truncated ? $"{body}...(truncated)" : body;
}

/// <summary>
/// OpenAI 风格错误对象。
/// </summary>
static object OpenAiError(string type, string message) => new
{
    error = new
    {
        type,
        message
    }
};

/// <summary>
/// 提取并脱敏关键请求头，输出日志摘要。
/// </summary>
static string BuildHeaderSummary(IHeaderDictionary headers)
{
    static string Mask(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Length <= 8) return "****";
        return $"{value[..4]}****{value[^4..]}";
    }

    var selected = new[] { "authorization", "content-type", "user-agent", "x-request-id" };
    var pairs = new List<string>();

    foreach (var key in selected)
    {
        if (!headers.TryGetValue(key, out var value)) continue;
        var raw = value.ToString();

        // 统一对 authorization/token 类字段做脱敏，避免凭据泄露。
        if (key.Contains("authorization", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            raw = Mask(raw);
        }

        pairs.Add($"{key}={raw}");
    }

    return string.Join(", ", pairs);
}
