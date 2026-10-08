namespace PromptCraft.Models;

/// <summary>
/// AI 提供商配置（T0.7，落地设计 §1.2 + CherryIN 风格 UI）。
/// 落 settings.db 表 provider_configs。
/// 模型列表拆到 provider_models 表，ProviderId 外键回查。
/// ApiKey 本期明文存储（用户已拍板；M5 前再评审 DPAPI）。
/// </summary>
public class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>显示名称（如 智谱 / CherryIN / DeepSeek）。</summary>
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>是否启用（对应截图右上角「启用」开关）。</summary>
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>该提供商下用户自行挑选的模型列表（Save 时随主表一起 upsert）。</summary>
    public List<ProviderModel> Models { get; set; } = new();
}

/// <summary>
/// provider_models：用户从 /models 拉到的模型池里手动挑选后加入的模型。
/// 每行带能力标记：是否用于扩写、是否用于反推、是否为该角色默认模型。
/// </summary>
public class ProviderModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProviderId { get; set; } = "";
    public string ModelName { get; set; } = "";

    /// <summary>扩写链路可用。</summary>
    public bool UseForExpand { get; set; }
    /// <summary>反推链路可用（视觉描述）。</summary>
    public bool UseForReverse { get; set; }
    /// <summary>扩写默认模型（同角色全局唯一）。</summary>
    public bool IsDefaultExpand { get; set; }
    /// <summary>反推默认模型（同角色全局唯一）。</summary>
    public bool IsDefaultReverse { get; set; }
}
