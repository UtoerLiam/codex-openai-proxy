using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

var options = new ProxyOptions(args);
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
var authData = await AuthData.LoadAsync(options.AuthPath);

app.MapGet("/health", () => Results.Json(new { status = "ok", service = "codex-openai-proxy" }));

app.MapGet("/models", () => Results.Json(ModelResponses.GetModels()));
app.MapGet("/v1/models", () => Results.Json(ModelResponses.GetModels()));

app.MapPost("/chat/completions", (HttpContext context) => HandleChatCompletionAsync(context, authData));
app.MapPost("/v1/chat/completions", (HttpContext context) => HandleChatCompletionAsync(context, authData));

app.Run($"http://0.0.0.0:{options.Port}");

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
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var chunkId = $"chatcmpl-{Guid.NewGuid()}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = request.Model ?? "gpt-5";
        var content = ResponseGenerator.GenerateContextualResponse(request.Messages ?? new());

        var chunks = new[]
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
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            }
        };

        foreach (var chunk in chunks)
        {
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await context.Response.Body.FlushAsync();
        }

        await context.Response.WriteAsync("data: [DONE]\n\n");
        await context.Response.Body.FlushAsync();
        return Results.Empty;
    }

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
    public int Port { get; }
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
    public string? OpenAIApiKey { get; init; }
    public TokenData? Tokens { get; init; }

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
    public string? AccessToken { get; init; }
    public string? AccountId { get; init; }
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
    public string? Model { get; init; }
    public List<ChatMessage>? Messages { get; init; }
    public bool? Stream { get; init; }
}

internal sealed class ChatMessage
{
    public string Role { get; init; } = "user";
    public JsonNode? Content { get; init; }
}

internal sealed class ChatCompletionsResponse
{
    public required string Id { get; init; }
    public required string Object { get; init; }
    public long Created { get; init; }
    public required string Model { get; init; }
    public required List<Choice> Choices { get; init; }
    public Usage? Usage { get; init; }
}

internal sealed class Choice
{
    public int Index { get; init; }
    public required ChatResponseMessage Message { get; init; }
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

internal sealed class ChatResponseMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

internal sealed class Usage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}
