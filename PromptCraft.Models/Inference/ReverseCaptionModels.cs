using System.Text.Json.Serialization;

namespace PromptCraft.Models.Inference;

// ============================================================
// 提示词反推 · 一比一对齐 PromptMaster 反推功能
// 基准源码：app/electron/service/joycaption.js、pm_prompt_engineering.js
//          config/captionPromptEngineering.js、captionLength.js、captionModels.js、
//          captionModelCapabilities.js、toriiGateFormats.js、torii_prompts_data.js、
//          tagLineSanitize.js、captionExtraOptionSanitize.js、joyCaptionExtraOptions.js
// 命名策略：与 PromptMaster caption 对象字段保持一致（snake_case 传输层）。
// ============================================================

/// <summary>
/// 反推输出格式（caption.type，12 种，与 PromptMaster joycaption.js CAPTION_TYPE_MAP 一致）。
/// 注意：ToriiGate 的 10 种结构化格式不属于 type，而是「提示词工程」（pe_torii_*），
/// 选择后 caption.type 会被 applyReverseToCaption 置为 "Structured"。
/// </summary>
public enum ReverseCType
{
    Descriptive,
    Descriptive_Casual,
    Straightforward,
    Stable_Diffusion_Prompt,
    MidJourney,
    Danbooru_tag_list,
    e621_tag_list,
    Rule34_tag_list,
    Booru_tag_list,
    Art_Critic,
    Product_Listing,
    Social_Media_Post,
}

/// <summary>打标对象（media_target），与 PromptMaster 前端「打标对象」一致。</summary>
public enum ReverseMediaTarget
{
    Image,
    Video,
    Mixed, // 图片+视频（自动）
}

/// <summary>提示词长度档位（caption.len），与 PromptMaster captionLength.js 一致。</summary>
public enum ReverseLengthKey
{
    VeryShort,
    Short,
    Medium,
    Long,
    VeryLong,
    Custom,
}

/// <summary>
/// 反推请求参数，字段与 PromptMaster joycaption.js runPromptMasterImageCaption / caption 对象逐一对应。
/// </summary>
public sealed class ReverseCaptionRequest
{
    // ---------- 基础 ----------
    /// <summary>输出格式（12 种 caption type），如 "Descriptive" / "Stable_Diffusion_Prompt"。</summary>
    public string Type { get; set; } = "Stable_Diffusion_Prompt";

    /// <summary>提示词工程 id（内置 3 + torii 10 + 自定义）。为空时按 mapLegacyCaptionType 从 Type 推导。</summary>
    public string? PeId { get; set; }

    /// <summary>输出语言：zh | en。</summary>
    public string CaptionLang { get; set; } = "zh";

    /// <summary>提示词长度档位（very_short/short/medium/long/very_long/custom）。</summary>
    public string Len { get; set; } = "medium";

    /// <summary>自定义目标字数（len=custom 时，1~20000 任意整数）。</summary>
    public int? CaptionLenChars { get; set; }

    /// <summary>显式 max_new_tokens（可选，覆盖篇幅计算）。</summary>
    public int? MaxNewTokens { get; set; }

    /// <summary>采样温度，null 时按模型能力矩阵取默认值。</summary>
    public double? Temperature { get; set; }

    /// <summary>采样 top_p，null 时按模型能力矩阵取默认值。</summary>
    public double? TopP { get; set; }

    /// <summary>自定义提示词（附加要求）。</summary>
    public string? ExtraPrompt { get; set; }

    // ---------- 质量提示词 ----------
    /// <summary>是否在标签前追加质量提示词。</summary>
    public bool QualityPromptEnabled { get; set; }

    /// <summary>质量词内容（拼接在标签行最前）。</summary>
    public string? QualityPromptPrefix { get; set; }

    // ---------- JoyCaption Extra 选项（joy_extra_options，4 项） ----------
    public bool JoySceneOnlyNoCharacterAppearance { get; set; }
    public bool JoyNoGlassesHeadwear { get; set; }
    public bool JoyNoArtisticStyle { get; set; }
    public bool JoyCharacterName { get; set; }

    /// <summary>外部传入的额外选项 id 列表（可选；与上方 bool 字段等价，优先使用本列表）。</summary>
    public List<string>? JoyExtraOptions { get; set; }

    /// <summary>JoyCaption 角色名（统一称呼选项的值）。</summary>
    public string? JoyCharacterNameValue { get; set; }

    /// <summary>强化 ANIMA 模式（SD / Danbooru 专用 PE）。</summary>
    public bool Anima3Enhance { get; set; }

    // ---------- ToriiGate 选项 ----------
    /// <summary>识别并使用角色名（默认 true）。</summary>
    public bool? ToriiUseNames { get; set; }

    /// <summary>注入我提供的 Booru 标签。</summary>
    public bool ToriiAddTags { get; set; }

    /// <summary>注入的 Booru 标签内容（逗号/换行分隔）。</summary>
    public string? ToriiGroundingTags { get; set; }

    /// <summary>注入的角色名列表内容。</summary>
    public string? ToriiGroundingCharacters { get; set; }

    // ---------- 媒体 ----------
    /// <summary>打标对象：image / video / mixed。</summary>
    public string MediaTarget { get; set; } = "image";

    /// <summary>媒体路径（单图/单视频）。批量时逐条传入。</summary>
    public string? MediaPath { get; set; }

    // ---------- 兼容旧 UI 字段（迁移期保留，UI 重写后移除） ----------
    [JsonIgnore]
    public string? ImageUrl { get => MediaPath; set => MediaPath = value; }

    [JsonIgnore]
    public string OutputLang { get => CaptionLang; set => CaptionLang = value; }

    [JsonIgnore]
    public string? CustomPrompt { get => ExtraPrompt; set => ExtraPrompt = value; }

    [JsonIgnore]
    public string? LengthKey { get => Len; set => Len = value ?? "medium"; }

    [JsonIgnore]
    public string? PromptEngine { get => PeId; set => PeId = value; }

    /// <summary>兼容旧 UI 枚举选择；服务端映射为 Type 字符串。</summary>
    [JsonIgnore]
    public ReverseCType? CType
    {
        get => Enum.TryParse<ReverseCType>(Type, out var c) ? c : null;
        set
        {
            if (value != null)
                Type = value.Value.ToString();
        }
    }

    /// <summary>视频抽帧后的帧路径（视频输入时由服务端填充）。</summary>
    public List<string>? FramePaths { get; set; }

    /// <summary>训练打标 sidecar：true 时写 .txt/.json 旁车。</summary>
    public bool WriteCaptionSidecar { get; set; }

    /// <summary>sidecar 文件格式：txt | json。</summary>
    public string CaptionFileFormat { get; set; } = "txt";

    // ---------- 服务端透传（由 Service 注入） ----------
    [JsonIgnore]
    public string? WorkspaceDir { get; set; }

    [JsonIgnore]
    public string? CaptionModel { get; set; } // 模型 key/名称，用于家族判定

    // ---------- applyReverseToCaption 输出（内部状态，由 PmPromptEngineeringService 写入） ----------
    [JsonIgnore]
    public string? PeOutputFormatValue { get; set; }

    [JsonIgnore]
    public bool PeBuiltin { get; set; }

    [JsonIgnore]
    public string? StructuredFormatValue { get; set; }

    [JsonIgnore]
    public string? ToriiFormatValue { get; set; }

    [JsonIgnore]
    public string? ToriiExtractModeValue { get; set; }

    [JsonIgnore]
    public string? PeCustomSystem { get; set; }

    [JsonIgnore]
    public string? PeCustomUserBody { get; set; }

    [JsonIgnore]
    public string? PeCustomOutputConstraints { get; set; }
}

/// <summary>反推单条结果。</summary>
public sealed class ReverseCaptionResult
{
    public string MediaPath { get; set; } = "";
    public string Caption { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double DurationSec { get; set; }
    public string? SidecarPath { get; set; }
}

/// <summary>批量反推进度事件（对应 pm_reverse_progress）。</summary>
public sealed class ReverseProgressEvent
{
    public int Index { get; set; }
    public int Total { get; set; }
    public string MediaPath { get; set; } = "";
    public string Status { get; set; } = ""; // processing | done | error | stopped
    public string? Caption { get; set; }
    public string? Error { get; set; }
}

// ============================================================
// 提示词工程（Prompt Engineering）模型
// 对应 PromptMaster config/promptEngineeringRegistry.js + pm_prompt_engineering.js
// ============================================================

/// <summary>提示词工程（PE）配置，持久化于 workspace/prompt_engineering.json。</summary>
public sealed class PromptEngineeringProfile
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "reverse"; // reverse | train | expand
    public bool Builtin { get; set; }
    public string BuiltinKey { get; set; } = "";
    public string CaptionType { get; set; } = "";
    public bool StructuredFormat { get; set; }
    public bool ToriiFormat { get; set; }
    public string ToriiExtractMode { get; set; } = "full";
    public bool? ToriiUseNamesDefault { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public string OutputFormat { get; set; } = "prose"; // prose | sd_tags | danbooru_tags | structured_md | structured_json
    public List<string> Tags { get; set; } = new();
    public List<string> SubjectDomains { get; set; } = new();

    // 自定义工程字段
    public string? SystemPrompt { get; set; }
    public string? UserPromptTemplate { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>ToriiGate 结构化格式元数据（对应 toriiGateFormats.js TORII_GATE_FORMATS）。</summary>
public sealed class ToriiGateFormat
{
    public string CType { get; set; } = "";         // long_thoughts_v2 / json / ...
    public string CTypeRuntime { get; set; } = "";  // 实际用于 PROMPTS_B 的键（min_structured_md_body 运行时用 min_structured_md）
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public bool UseNamesDefault { get; set; }
    public string ExtractMode { get; set; } = "full"; // full | min_md_body | json_raw | json_flat
    public int Sort { get; set; }
}

/// <summary>模型能力矩阵项（对应 captionModelCapabilities.js getCaptionModelCapabilities）。</summary>
public sealed class CaptionModelCapabilities
{
    public List<string> MediaTargets { get; set; } = new(); // image | video | mixed
    public string? LockedOutputLang { get; set; }           // en（torii 锁定英文）
    public double? DefaultTemperature { get; set; }
    public double? DefaultTopP { get; set; }
}

/// <summary>反推调用所需的组装提示词（system/user），对应 buildToriiPrompts 等返回值。</summary>
public sealed class CaptionPrompts
{
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public string? MaxTokensHint { get; set; }
}
