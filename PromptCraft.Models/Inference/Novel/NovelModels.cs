namespace PromptCraft.Models.Inference.Novel;

/// <summary>流水线阶段（7 阶段，S5/S6/S7 有结构化产出）。</summary>
public enum NovelStage
{
    Concept = 1,
    Characters = 2,
    Worldbuilding = 3,
    Treatment = 4,
    AssetScan = 5,
    ShotPlanning = 6,
    Prompts = 7,
}

/// <summary>逐镜提示词输出格式（索引即存档值，禁止重排；新格式追加到末尾）。</summary>
public enum NovelOutputFormat
{
    ChineseDirect = 0,   // H3 中文直投（默认）
    H3FullReference = 1, // MiniMax 六段式通用提示词（与扩写页 MinimaxH3+六段式通用提示词一致）
    Seedance20 = 2,      // Seedance 2.0
    H3DirectorStory = 3, // MiniMax 连续剧情（导演台）（与扩写页连续剧情（导演台）一致）
}

/// <summary>镜头粒度。</summary>
public enum NovelGranularity
{
    PerShot = 0,         // 逐镜头（默认）
    PerSceneMerged = 1,  // 逐场合并宽松预览
}

/// <summary>审查方式。</summary>
public enum NovelReviewMode
{
    AllAtOnce = 0,       // 一次生成全部逐条编辑（默认）
    SceneByScene = 1,    // 逐场滚动
}

/// <summary>流水线输出文本语言（只提供中文/English，强制所有输出使用所选语言）。</summary>
public enum NovelOutputLanguage
{
    Chinese = 0,
    English = 1,
}

/// <summary>流水线全部选项（均经 pm_novel_* 记忆，下次打开默认加载上次值）。</summary>
public sealed class NovelPromptOptions
{
    // 各阶段开关，默认全开
    public bool EnableConcept { get; set; } = true;
    public bool EnableCharacters { get; set; } = true;
    public bool EnableWorldbuilding { get; set; } = true;
    public bool EnableTreatment { get; set; } = true;
    public bool EnableAssetScan { get; set; } = true;
    public bool EnableShotPlanning { get; set; } = true;
    public bool EnablePrompts { get; set; } = true;

    // 格式 / 粒度 / 审查
    public NovelOutputFormat OutputFormat { get; set; } = NovelOutputFormat.ChineseDirect;
    public NovelGranularity Granularity { get; set; } = NovelGranularity.PerShot;
    public NovelReviewMode ReviewMode { get; set; } = NovelReviewMode.AllAtOnce;
    public NovelOutputLanguage OutputLanguage { get; set; } = NovelOutputLanguage.Chinese;
    // 无参考图降级：六段式格式下，开启且本镜头无可用资产图时，改用官方三字段 T2VA（integrated_multimodal_description/overall_soundscape/non_diegetic_music）；默认关闭保持六段式
    public bool EnableNoRefT2VA { get; set; } = false;

    // 资产扫描（文件名模糊匹配为主；可选 LLM 归类）
    public bool AssetLlmClassify { get; set; } = false;

    // 缺口提示词生成（S5 内子步骤：图像缺口→ComfyUI 资产图提示词 / 音色缺口→VoiceStudio 音色卡；分批避免单次输出超上下文）
    public bool EnableGapPrompt { get; set; } = true;
    public int ImageBatchSize { get; set; } = 5;
    public int VoiceBatchSize { get; set; } = 10;

    // 知识库（可选：给 LLM 提供文本方法的目录，默认不查；v1 = 目录枚举+关键词检索+文件名注入）
    public bool KnowledgeBaseEnabled { get; set; } = false;
    public string KnowledgeBasePath { get; set; } = "";
    // 资产目录（可选：存放角色/场景/道具 Lira 图片，供 S5 资产扫描比对；与知识库目录分离）
    public string AssetDirectory { get; set; } = "";

    // 视频参数（画幅 + 每段大概时长；整场时长预算由 skill 按内容自动核算，用户不填）
    public string AspectRatio { get; set; } = "16:9";
    public double DefaultShotSeconds { get; set; } = 6;

    // 模型选择（用户在页面选择；留空则回落扩写默认模型）
    public string ProviderId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

/// <summary>镜头项（S6 镜头表产物，S7 据此逐镜生成）。</summary>
public sealed class ShotItem
{
    public string ShotId { get; set; } = "";
    public string SourceScene { get; set; } = "";
    public string Purpose { get; set; } = "";
    public double Duration { get; set; }
    public string ShotSize { get; set; } = "";
    public string CameraPosition { get; set; } = "";
    public string FocalLength { get; set; } = "";
    public string CameraMovement { get; set; } = "";
    public string WorldPosition { get; set; } = "";
    public string ScreenPosition { get; set; } = "";
    public string Gaze { get; set; } = "";
    public string AxisSide { get; set; } = "";
    public string Action { get; set; } = "";
    public string Performance { get; set; } = "";
    public string PropsState { get; set; } = "";
    public string Dialogue { get; set; } = "";
    public int DialogueCharCount { get; set; }
    public double DialogueSeconds { get; set; }
    public string AssetsUsed { get; set; } = "";
    public string FirstFrame { get; set; } = "";
    public string LastFrame { get; set; } = "";
    public string ContinuityRisk { get; set; } = "";
    public string LongDurationReason { get; set; } = "";
}

/// <summary>S5 资产扫描结果。</summary>
public sealed class AssetScanResult
{
    public List<string> ConfirmedAssets { get; set; } = new(); // 从小说/分场确认的角色/场景/道具
    public List<string> AvailableFiles { get; set; } = new();  // 资产目录枚举到的图片文件
    public List<string> MatchedFiles { get; set; } = new();    // 已匹配的资产图片文件路径
    public List<string> Gaps { get; set; } = new();            // 缺失图像资产（提示补齐）
    public List<string> VoiceAssets { get; set; } = new();     // 已确认语音资产（角色圣经角色，每人一个）
    public List<string> AudioFiles { get; set; } = new();      // 资产目录枚举到的音频文件
    public List<string> VoiceGaps { get; set; } = new();       // 缺失音色（角色名，需在 VoiceStudio 生成）
    public string RawText { get; set; } = "";                  // 展示给用户的可编辑文本
}

/// <summary>S5 缺口提示词分批结果（一次一批，避免单次输出超上下文）。</summary>
public sealed class GapPromptBatch
{
    public int Processed { get; set; }              // 已处理缺口累计数（含本批）
    public int Total { get; set; }                  // 缺口总数
    public bool HasMore { get; set; }               // 是否还有下一批
    public List<string> Prompts { get; set; } = new(); // 本批生成的提示词行（「资产名：提示词」）
}

/// <summary>
/// 流水线分批执行进度回调（S6 镜头规划 / S7 逐镜提示词分批时，每批完成后立即回传，供 UI 实时追加结果与更新进度）。
/// </summary>
public sealed class NovelBatchProgress
{
    /// <summary>本批结果文本（已含展示格式头，调用方直接追加到输出区；空串表示仅更新进度不追加）。</summary>
    public string Text { get; set; } = "";
    /// <summary>阶段内已完成单元数（镜头数或场次数）。</summary>
    public int Done { get; set; }
    /// <summary>阶段内总单元数（未知时为 -1，进度条按不确定进度显示）。</summary>
    public int Total { get; set; } = -1;
    /// <summary>进度条文案（如「第 3/5 批」「镜头 12/30」），null 不显示。</summary>
    public string? ProgressText { get; set; }
}

/// <summary>S6 镜头规划结果。</summary>
public sealed class ShotPlanResult
{
    public List<SceneTiming> SceneTimings { get; set; } = new();
    public List<ShotItem> Shots { get; set; } = new();
    public string RawText { get; set; } = ""; // 镜头表文本（可编辑）
}

public sealed class SceneTiming
{
    public string SceneId { get; set; } = "";
    public double BudgetSeconds { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>S7 逐镜提示词产物。</summary>
public sealed class PromptResult
{
    public string ShotId { get; set; } = "";
    public string SourceScene { get; set; } = "";
    public string Model { get; set; } = "";  // MiniMaxH3 / Seedance2.0
    public string Prompt { get; set; } = ""; // 提示词正文（单镜头，独立可理解）
}

/// <summary>单阶段结果（VM 展示可编辑文本，并携带结构化负载）。</summary>
public sealed class NovelStageResult
{
    public NovelStage Stage { get; set; }
    public string Text { get; set; } = "";
    public AssetScanResult? AssetScan { get; set; }
    public ShotPlanResult? ShotPlan { get; set; }
}

/// <summary>流水线上下文：累计各阶段已确认输出，供后续阶段读取。</summary>
public sealed class NovelPipelineContext
{
    public string NovelText { get; set; } = "";
    public NovelPromptOptions Options { get; set; } = new();
    public string? Concept { get; set; }
    public string? Characters { get; set; }
    public string? Worldbuilding { get; set; }
    public string? Treatment { get; set; }
    public List<string> ConfirmedAssets { get; set; } = new();
    public ShotPlanResult? ShotPlan { get; set; }
    public List<PromptResult> Prompts { get; set; } = new();
}
