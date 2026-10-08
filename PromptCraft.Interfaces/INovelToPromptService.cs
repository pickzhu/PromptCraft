using PromptCraft.Models.Inference.Novel;

namespace PromptCraft.Interfaces;

/// <summary>
/// 小说 → MiniMaxH3 / Seedance 提示词流水线服务。
/// S1 概念 / S2 角色 / S3 世界观 / S4 分场 走 RunStageAsync（单文本、人工确认）；
/// S5 资产扫描 / S6 镜头规划 / S7 逐镜提示词 走结构化方法。
/// 无状态：每次传入上下文（含各阶段已确认输出），由 VM 按阶段逐步调用并回填。
/// </summary>
public interface INovelToPromptService
{
    /// <summary>运行 S1-S4 单文本阶段（Concept/Characters/Worldbuilding/Treatment），返回可编辑文本。</summary>
    Task<NovelStageResult> RunStageAsync(NovelStage stage, NovelPipelineContext ctx, CancellationToken ct);

    /// <summary>S5 资产扫描：枚举资产目录（图片+音频） + 与已确认资产做文件名模糊匹配，给出缺失提示（含音色缺口）。</summary>
    Task<AssetScanResult> ScanAssetsAsync(NovelPipelineContext ctx, CancellationToken ct);

    /// <summary>
    /// S5 缺口提示词分批生成：一次只处理一批（offset 起，图像缺口→ComfyUI 资产图提示词；音色缺口→VoiceStudio 音色卡），
    /// 返回本批结果与是否还有下一批，由调用方推进 offset。
    /// </summary>
    Task<GapPromptBatch> GenerateGapPromptsAsync(NovelPipelineContext ctx,
        IReadOnlyList<string> imageGaps, IReadOnlyList<string> voiceGaps, int offset, CancellationToken ct);

    /// <summary>
    /// S6 镜头规划门：场次时长核算 + 镜头表（含连续性），必须由用户确认后才进 S7。
    /// onProgress：分批/逐场完成时回传本批结果与进度，供 UI 实时追加显示（可为 null）。
    /// </summary>
    Task<ShotPlanResult> PlanShotsAsync(NovelPipelineContext ctx, CancellationToken ct, Action<NovelBatchProgress>? onProgress = null);

    /// <summary>
    /// S7 逐镜提示词生成：按 ShotPlan.Shots 逐镜产出（H3 中文直投 / H3 官方六段 / Seedance 2.0）。
    /// onProgress：每条/每子批完成时回传本批结果与进度，供 UI 实时追加显示（可为 null）。
    /// </summary>
    Task<IReadOnlyList<PromptResult>> GeneratePromptsAsync(NovelPipelineContext ctx, CancellationToken ct, Action<NovelBatchProgress>? onProgress = null);

    /// <summary>知识库检索：目录枚举 + 关键词过滤，返回候选文件路径列表。</summary>
    Task<IReadOnlyList<string>> SearchKnowledgeBaseAsync(string kbPath, string keyword, CancellationToken ct);
}
