using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// 启动参数配置：负责解析端口和认证文件路径。
var options = new ProxyOptions(args);
// 创建 ASP.NET Core Minimal API 应用构建器。
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
// 启动时预加载认证信息，避免每个请求都去重复读取磁盘。
var authData = await AuthData.LoadAsync(options.AuthPath);

// 健康检查接口，用于容器探活和外部监控。
app.MapGet("/health", () => Results.Json(new { status = "ok", service = "codex-openai-proxy" }));

// 同时兼容 /models 与 /v1/models 两种路径，方便不同客户端直接接入。
app.MapGet("/models", () => Results.Json(ModelResponses.GetModels()));
app.MapGet("/v1/models", () => Results.Json(ModelResponses.GetModels()));

// 同时兼容 OpenAI 常见的两个 chat completions 路径。
app.MapPost("/chat/completions", (HttpContext context) => HandleChatCompletionAsync(context, authData));
app.MapPost("/v1/chat/completions", (HttpContext context) => HandleChatCompletionAsync(context, authData));

app.Run($"http://0.0.0.0:{options.Port}");

/// <summary>
/// 处理 chat completions 请求。
/// 说明：
/// 1) 支持非流式（一次性返回）
/// 2) 支持流式（SSE 分块返回）
/// 3) 响应内容使用简单规则生成，便于本地联调与代理连通性验证
/// </summary>
static async Task<IResult> HandleChatCompletionAsync(HttpContext context, AuthData authData)
{
    ChatCompletionsRequest? request;

    try
    {
        request = await JsonSerializer.DeserializeAsync<ChatCompletionsRequest>(context.Request.Body);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON payload.");
    }

    if (request is null)
    {
        return Results.BadRequest("Request body is required.");
    }

    if (request.Stream ?? false)
    {
        // 配置 SSE（Server-Sent Events）响应头。
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var chunkId = $"chatcmpl-{Guid.NewGuid()}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = request.Model ?? "gpt-5";
        var content = ResponseGenerator.GenerateContextualResponse(request.Messages ?? new());

        // 注意：这里显式声明为 object[]，因为三个 chunk 的 delta 结构并不完全一致。
        // 如果使用 new[] 让编译器推断匿名类型，会因匿名对象结构不一致导致编译错误。
        var chunks = new object[]
        {
            new
            {
                id = chunkId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } }
            },
            new
            {
                id = chunkId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, delta = new { content }, finish_reason = (string?)null } }
            },
            new
            {
                id = chunkId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, delta = new { }, finish_reason = (string?)"stop" } }
            }
        };

        // 逐块写出 SSE 数据，每一块都遵循 "data: ...\n\n" 规范。
        foreach (var chunk in chunks)
        {
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await context.Response.Body.FlushAsync();
        }

        await context.Response.WriteAsync("data: [DONE]\n\n");
        await context.Response.Body.FlushAsync();
        return Results.Empty;
    }

    // 非流式模式下，一次性返回标准的 chat.completion 结构。
    var response = new ChatCompletionsResponse
    {
        Id = $"chatcmpl-{Guid.NewGuid()}",
        Object = "chat.completion",
        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Model = request.Model ?? "gpt-5",
        Choices =
        [
            new Choice
            {
                Index = 0,
                Message = new ChatResponseMessage
                {
                    Role = "assistant",
                    Content = "I can help you with coding tasks! The C# proxy connection is working well."
                },
                FinishReason = "stop"
            }
        ],
        Usage = new Usage
        {
            PromptTokens = 50,
            CompletionTokens = 30,
            TotalTokens = 80
        }
    };

    return Results.Json(response);
}

internal sealed class ProxyOptions
{
    /// <summary>
    /// 服务监听端口，默认 8080。
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// 认证信息文件路径，默认 ~/.codex/auth.json。
    /// </summary>
    public string AuthPath { get; }

    public ProxyOptions(string[] args)
    {
        Port = 8080;
        AuthPath = "~/.codex/auth.json";

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--port" or "-p")
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
                {
                    Port = port;
                    i++;
                }
            }
            else if (args[i] == "--auth-path" && i + 1 < args.Length)
            {
                AuthPath = args[i + 1];
                i++;
            }
        }
    }
}

internal sealed class AuthData
{
    /// <summary>
    /// 兼容 OPENAI_API_KEY 形式的密钥字段。
    /// </summary>
    public string? OpenAIApiKey { get; init; }

    /// <summary>
    /// OAuth/会话相关 token 信息。
    /// </summary>
    public TokenData? Tokens { get; init; }

    /// <summary>
    /// 从指定路径加载认证信息。
    /// 支持使用 ~/ 前缀表示用户主目录。
    /// </summary>
    public static async Task<AuthData> LoadAsync(string authPath)
    {
        var expandedPath = authPath.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), authPath[2..])
            : authPath;

        if (!File.Exists(expandedPath))
        {
            return new AuthData();
        }

        var json = await File.ReadAllTextAsync(expandedPath);
        var root = JsonNode.Parse(json)?.AsObject();

        return new AuthData
        {
            OpenAIApiKey = root?["OPENAI_API_KEY"]?.GetValue<string>(),
            Tokens = root?["tokens"] is JsonObject tokenObj
                ? new TokenData
                {
                    AccessToken = tokenObj["access_token"]?.GetValue<string>(),
                    AccountId = tokenObj["account_id"]?.GetValue<string>(),
                    RefreshToken = tokenObj["refresh_token"]?.GetValue<string>()
                }
                : null
        };
    }
}

internal sealed class TokenData
{
    /// <summary>
    /// 访问令牌。
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>
    /// 账户标识。
    /// </summary>
    public string? AccountId { get; init; }

    /// <summary>
    /// 刷新令牌。
    /// </summary>
    public string? RefreshToken { get; init; }
}

internal static class ModelResponses
{
    public static object GetModels() => new
    {
        @object = "list",
        data = new[]
        {
            new { id = "gpt-4", @object = "model", created = 1687882411, owned_by = "openai" },
            new { id = "gpt-5", @object = "model", created = 1687882411, owned_by = "openai" }
        }
    };
}

internal static class ResponseGenerator
{
    /// <summary>
    /// 根据最近一条 user 消息生成上下文回复。
    /// 当前实现是启发式规则，目标是用于联调，不追求复杂语义能力。
    /// </summary>
    public static string GenerateContextualResponse(List<ChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == "user");
        var content = lastUser?.Content;

        if (content is null)
        {
            return "I'm ready to help with coding tasks, debugging, and implementation questions.";
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            var lowered = text.ToLowerInvariant();
            if (lowered.Contains("hello") || lowered.Contains("hi"))
            {
                return "Hello! I can help you with coding tasks, debugging, and software development questions.";
            }

            if (lowered.Contains("fix") || lowered.Contains("bug") || lowered.Contains("error"))
            {
                return "I'd be happy to help fix bugs and errors. Share the error details and code context.";
            }

            return "I can help with your request. Please share more implementation details so I can assist precisely.";
        }

        return "I can see your request context and I'm ready to help with the next coding step.";
    }
}

internal sealed class ChatCompletionsRequest
{
    /// <summary>
    /// 客户端请求的模型名。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// 对话消息列表。
    /// </summary>
    public List<ChatMessage>? Messages { get; init; }

    /// <summary>
    /// 是否启用流式返回。
    /// </summary>
    public bool? Stream { get; init; }
}

internal sealed class ChatMessage
{
    /// <summary>
    /// 消息角色（如 user / assistant / system）。
    /// </summary>
    public string Role { get; init; } = "user";

    /// <summary>
    /// 消息内容，使用 JsonNode 以兼容字符串或多模态结构。
    /// </summary>
    public JsonNode? Content { get; init; }
}

internal sealed class ChatCompletionsResponse
{
    /// <summary>
    /// 响应唯一 ID。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 对象类型，通常为 chat.completion。
    /// </summary>
    public required string Object { get; init; }

    /// <summary>
    /// Unix 秒级时间戳。
    /// </summary>
    public long Created { get; init; }

    /// <summary>
    /// 实际返回所使用的模型名称。
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// 候选结果列表。
    /// </summary>
    public required List<Choice> Choices { get; init; }

    /// <summary>
    /// token 使用统计。
    /// </summary>
    public Usage? Usage { get; init; }
}

internal sealed class Choice
{
    /// <summary>
    /// 当前候选在 choices 中的索引。
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// assistant 消息内容。
    /// </summary>
    public required ChatResponseMessage Message { get; init; }
    [JsonPropertyName("finish_reason")]

    /// <summary>
    /// 结束原因（例如 stop / length / content_filter）。
    /// </summary>
    public string? FinishReason { get; init; }
}

internal sealed class ChatResponseMessage
{
    /// <summary>
    /// 消息角色。
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// 消息文本。
    /// </summary>
    public required string Content { get; init; }
}

internal sealed class Usage
{
    /// <summary>
    /// 输入 token 数。
    /// </summary>
    public int PromptTokens { get; init; }

    /// <summary>
    /// 输出 token 数。
    /// </summary>
    public int CompletionTokens { get; init; }

    /// <summary>
    /// 总 token 数。
    /// </summary>
    public int TotalTokens { get; init; }
}
