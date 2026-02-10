using System.Text.Json;

namespace CodexOpenAIProxy;

/// <summary>
/// 负责从 Codex 的 auth.json 中提取可用 token。
/// 设计目标：
/// 1) 尽可能兼容未知结构（对象、数组、多 profile 混合）。
/// 2) 优先命中常见字段名（token/api_key/apiKey/access_token）。
/// 3) 仅返回命中路径，不泄露敏感值。
/// </summary>
public static class AuthLoader
{
    /// <summary>
    /// 按优先级匹配 token 字段。
    /// </summary>
    private static readonly string[] TokenKeys = ["token", "api_key", "apiKey", "access_token"];

    /// <summary>
    /// 获取默认 auth.json 路径。
    /// Windows/Linux/macOS 都基于用户主目录拼接 ~/.codex/auth.json。
    /// </summary>
    public static string GetDefaultAuthPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex", "auth.json");
    }

    /// <summary>
    /// 从指定路径加载并解析 token。
    /// </summary>
    public static async Task<AuthLoadResult> LoadTokenAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Codex auth file not found: {path}");
        }

        await using var fs = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(fs);

        var found = FindFirstToken(doc.RootElement, "$", 0);
        if (found is null)
        {
            throw new InvalidOperationException("No usable token found in auth.json. Expected one of: token, api_key, apiKey, access_token.");
        }

        return found;
    }

    /// <summary>
    /// 深度优先遍历 JSON，找到第一个可用 token。
    /// depth 上限用于规避异常/恶意深层结构导致的递归风险。
    /// </summary>
    private static AuthLoadResult? FindFirstToken(JsonElement element, string entryName, int depth)
    {
        if (depth > 32)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                // 先在当前对象层查 token 字段，命中后立即返回。
                foreach (var key in TokenKeys)
                {
                    if (element.TryGetProperty(key, out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String)
                    {
                        var token = tokenElement.GetString();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            return new AuthLoadResult(token, $"{entryName}.{key}");
                        }
                    }
                }

                // 当前层未命中，继续递归其属性。
                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindFirstToken(property.Value, $"{entryName}.{property.Name}", depth + 1);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;
            }
            case JsonValueKind.Array:
            {
                // 对数组逐项遍历，优先取第一个可用 token。
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindFirstToken(item, $"{entryName}[{index}]", depth + 1);
                    if (nested is not null)
                    {
                        return nested;
                    }

                    index++;
                }

                return null;
            }
            default:
                return null;
        }
    }
}

/// <summary>
/// token 解析结果：
/// - Token: 实际用于上游鉴权的值
/// - EntryName: 命中路径（仅用于日志诊断）
/// </summary>
public sealed record AuthLoadResult(string Token, string EntryName);
