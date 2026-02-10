namespace CodexOpenAIProxy;

/// <summary>
/// 模型映射器：
/// - 下游（Cursor/OpenAI 协议）模型名 -> 上游（Codex）模型名。
/// - 当前使用硬编码字典，便于后续替换为配置文件/数据库。
/// </summary>
public sealed class ModelMapper
{
    private readonly Dictionary<string, string> _modelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4.1"] = "codex-gpt-4.1",
        ["gpt-4o"] = "codex-gpt-4o",
        ["gpt-5"] = "codex-gpt-5"
    };

    /// <summary>
    /// 模型名映射规则：
    /// 1) 空模型名时提供默认值。
    /// 2) 在映射表中命中则返回上游模型。
    /// 3) 未命中时原样透传，减少兼容性阻塞。
    /// </summary>
    public string Map(string? externalModel)
    {
        if (string.IsNullOrWhiteSpace(externalModel))
        {
            return "codex-gpt-4.1";
        }

        return _modelMap.TryGetValue(externalModel, out var upstream) ? upstream : externalModel;
    }

    /// <summary>
    /// 对外暴露可见模型列表（用于 /v1/models）。
    /// </summary>
    public IReadOnlyCollection<string> GetExternalModels() => _modelMap.Keys;
}
