using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using PromptCraft.Models.Inference.Novel;
using PromptCraft.Service.Documents;
using PromptCraft.Service.Inference.Minimax;
using Ke.Bee.Localization.Localizer;
using Microsoft.Extensions.DependencyInjection;

namespace PromptCraft.Service.Inference.Novel;

/// <summary>
/// 小说→提示词流水线编排（无状态，每阶段传入上下文）。
/// S1-S4 单文本阶段走 LLM（知识库启用时注入目录文本文件名作参考）；
/// S5 资产扫描（先 LLM 提取资产需求表 → 扫「资产目录」比对 → 报缺失；缺口提示词分批输出 + 截断续写/缺失补漏）；
/// S6 镜头规划门（按场次逐场生成镜头 JSON，长场按 ≤5 镜/批分批输出并拼装，杜绝截断导致后续无法执行）；
/// S7 逐镜提示词（按输出格式：H3 中文直投 / Minimax六段式通用提示词（full_reference）/ 连续剧情（导演台）（director_segments）/ Seedance 2.0；单条截断自动续写，逐场合并按子批输出）。
/// 六段式与导演台复用扩写页 MiniMaxAssembler 同一场景组装（pe_expand_h3_full_reference / pe_expand_minimax_continuous_story），输出格式与扩写页对应选项完全一致。
/// 输出语言：全部输出强制使用选项所选语言（中文/English）。
/// 模型：页面已选模型优先，未选回落扩写默认；温度 0.7 / top_p 0.9；输出走 StripThinkingTags 清洗。
/// </summary>
public sealed class NovelToPromptService : INovelToPromptService
{
    private readonly IProviderService _providerSvc;
    private const int StageMaxTokens = 4096;
    private const int ShotMaxTokens = 4096;
    private const int ShotPlanMaxTokens = 8192;   // 单场镜头表每批上限（长场分批后每批不超过该值）
    private const int ShotPlanEstimateMaxTokens = 128; // 镜头总数预估调用输出上限（只回一个整数）
    private const int AssetExtractMaxTokens = 4096;
    private const int ShotPlanBatchSize = 5;        // 镜头规划分批：每批镜头数上限（长场防截断）
    private const int ShotPlanMaxBatches = 30;      // 单场最大分批数（防死循环）
    private const double ShotDuplicateThreshold = 0.9; // 镜头内容去重：规范化签名相似度阈值
    private const int LongSceneCharThreshold = 1500; // 分场文本超过该长度直接走分批生成
    private const int MergedSubBatchSize = 4;       // 逐场合并提示词子批：每批镜头数上限
    private const int GapPromptMaxContinue = 2;     // 缺口批次输出截断时最大续写轮数

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac" };

    private static readonly HashSet<string> KnowledgeExts = new(StringComparer.OrdinalIgnoreCase)
    { ".md", ".txt", ".docx", ".pdf" };

    private readonly KnowledgeDocumentReader? _docReader;

    public NovelToPromptService(IServiceProvider services)
    {
        _providerSvc = services.GetRequiredService<IProviderService>();
        _docReader = services.GetService<KnowledgeDocumentReader>();
    }

    // ============================================================
    // S1-S4 单文本阶段
    // ============================================================
    public async Task<NovelStageResult> RunStageAsync(NovelStage stage, NovelPipelineContext ctx, CancellationToken ct)
    {
        if (stage is NovelStage.AssetScan or NovelStage.ShotPlanning or NovelStage.Prompts)
            throw new ArgumentException($"阶段 {stage} 使用专用方法执行", nameof(stage));
        if (string.IsNullOrWhiteSpace(ctx.NovelText))
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["NovelSourceRequired"] ?? "请先输入小说原文");

        var system = NovelPromptEngineering.BuildStageSystem(stage, ctx.Options);
        var user = NovelPromptEngineering.BuildStageUser(stage, ctx);
        var kbBlock = await BuildKnowledgeRefsBlockAsync(ctx.Options);
        if (!string.IsNullOrEmpty(kbBlock))
            user += "\n" + kbBlock;

        var sw = Stopwatch.StartNew();
        LogService.Instance.Info($"小说流水线阶段 {stage} 开始", "Novel");
        var text = await CallChatAsync(system, user, StageMaxTokens, ctx.Options, ct);
        sw.Stop();
        LogService.Instance.Info($"小说流水线阶段 {stage} 完成，耗时 {sw.Elapsed.TotalSeconds:0.0}s，输出 {text.Length} 字符", "Novel");

        return new NovelStageResult { Stage = stage, Text = text };
    }

    /// <summary>
    /// 知识库参考注入（v2 深度解析）：文件名清单 + 各文件解析摘要。
    /// 支持 .md/.txt（直读）、.docx（ZIP 解包）、.pdf（渲染 + 离线 OCR，含扫描件）。
    /// 摘要经 OCR/解析缓存复用，避免各阶段重复解析；单文件摘要在 400 字符内，总注入 ≤ 8000 字符。
    /// </summary>
    private async Task<string> BuildKnowledgeRefsBlockAsync(NovelPromptOptions opts)
    {
        if (!opts.KnowledgeBaseEnabled || string.IsNullOrWhiteSpace(opts.KnowledgeBasePath) || !Directory.Exists(opts.KnowledgeBasePath))
            return "";
        try
        {
            var files = Directory.EnumerateFiles(opts.KnowledgeBasePath, "*", SearchOption.TopDirectoryOnly)
                .Where(f => KnowledgeExts.Contains(Path.GetExtension(f)))
                .ToList();
            if (files.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("【本地知识库参考文件】（只作创作方法参考，禁止照搬其中的人物/剧情/对白）：");
            foreach (var f in files.Take(10))
            {
                sb.AppendLine("- " + Path.GetFileName(f));
                if (_docReader == null) continue;
                var text = await _docReader.ExtractTextAsync(f);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var snippet = text.ReplaceLineEndings(" ").Trim();
                if (snippet.Length > 400) snippet = snippet[..400] + "…";
                sb.Append("    摘要：").AppendLine(snippet);
                if (sb.Length >= 8000)
                {
                    sb.AppendLine("    …（其余文件略）");
                    break;
                }
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    // ============================================================
    // S5 资产扫描
    // ============================================================
    public async Task<AssetScanResult> ScanAssetsAsync(NovelPipelineContext ctx, CancellationToken ct)
    {
        var result = new AssetScanResult();
        var sw = Stopwatch.StartNew();

        // 1) 已确认资产清单：优先取 ctx.ConfirmedAssets；为空则先由 LLM 从小说/分场/角色提取资产需求表
        if (ctx.ConfirmedAssets.Count > 0)
        {
            result.ConfirmedAssets.AddRange(DedupAssets(ctx.ConfirmedAssets));
            LogService.Instance.Info($"资产扫描：使用已确认资产 {result.ConfirmedAssets.Count} 项", "Novel");
        }
        else
        {
            var extracted = await ExtractAssetsWithLlmAsync(ctx, ct);
            result.ConfirmedAssets.AddRange(DedupAssets(extracted));
            LogService.Instance.Info($"资产扫描：LLM 提取资产需求 {result.ConfirmedAssets.Count} 项", "Novel");
        }

        // 2) 资产目录枚举（独立于知识库；图片 + 音频）
        var assetDir = ctx.Options.AssetDirectory;
        var dirOk = !string.IsNullOrWhiteSpace(assetDir) && Directory.Exists(assetDir);
        if (dirOk)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(assetDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(f);
                    if (ImageExts.Contains(ext)) result.AvailableFiles.Add(f);
                    else if (AudioExts.Contains(ext)) result.AudioFiles.Add(f);
                }
            }
            catch { /* 目录不可读则跳过 */ }
        }

        // 3) 文件名模糊匹配：文件名包含任一已确认资产关键字即视为命中
        foreach (var file in result.AvailableFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var hit = result.ConfirmedAssets.Any(a => !string.IsNullOrWhiteSpace(a) && name.Contains(a, StringComparison.OrdinalIgnoreCase));
            if (hit) result.MatchedFiles.Add(file);
        }

        // 4) 缺失资产：已确认但未匹配到文件的
        result.Gaps = ComputeGaps(result.ConfirmedAssets, result.MatchedFiles);

        // 4.5) 语音资产（每人一个音色）：从角色圣经提取角色清单，与音频文件比对出音色缺口
        result.VoiceAssets = await ExtractVoicesWithLlmAsync(ctx, ct);
        result.VoiceGaps = ComputeVoiceGaps(result.VoiceAssets, result.AudioFiles);

        // 5) 可选 LLM 归类（文件名语义归类）：默认关，用户开启时对未匹配文件做一次语义归类补漏
        if (ctx.Options.AssetLlmClassify)
        {
            var unMatched = result.AvailableFiles.Except(result.MatchedFiles).ToList();
            if (unMatched.Count > 0)
            {
                var classified = await ClassifyAssetsWithLlmAsync(unMatched, result.ConfirmedAssets, ctx.Options, ct);
                foreach (var c in classified)
                    if (!result.MatchedFiles.Contains(c))
                        result.MatchedFiles.Add(c);
                result.Gaps = ComputeGaps(result.ConfirmedAssets, result.MatchedFiles);
            }
        }

        sw.Stop();
        LogService.Instance.Info(
            $"资产扫描完成：资产 {result.ConfirmedAssets.Count} 项，语音资产 {result.VoiceAssets.Count} 个，目录文件 {result.AvailableFiles.Count} 个（音频 {result.AudioFiles.Count}），匹配 {result.MatchedFiles.Count} 个，缺失图像 {result.Gaps.Count} 项，缺失音色 {result.VoiceGaps.Count} 个，耗时 {sw.Elapsed.TotalSeconds:0.0}s",
            "Novel");

        result.RawText = BuildAssetScanText(result, dirOk);
        return result;
    }

    /// <summary>用 LLM 从小说/分场/角色提取资产需求（角色/场景/道具），返回候选关键字（资产名+命名建议）。</summary>
    private async Task<List<string>> ExtractAssetsWithLlmAsync(NovelPipelineContext ctx, CancellationToken ct)
    {
        try
        {
            LogService.Instance.Info("资产扫描：开始 LLM 资产需求提取", "Novel");
            var raw = await CallChatAsync(
                NovelPromptEngineering.BuildAssetExtractionSystem(ctx.Options.OutputLanguage),
                NovelPromptEngineering.BuildAssetExtractionUser(ctx),
                AssetExtractMaxTokens, ctx.Options, ct);

            var assets = new List<string>();
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim().TrimStart('-', '*', '•', ' ', '\t');
                if (t.Length == 0) continue;
                if (t.StartsWith("【") || t.StartsWith("#")) continue; // 分类标题
                var parts = t.Split('|')
                    .Select(p => p.Trim().Trim('"', '「', '」', '：', ':'))
                    .Where(p => p.Length > 0)
                    .ToList();
                if (parts.Count == 0) continue;
                // 类型列（第 2 列）白名单过滤：缺失资产只允许 角色/场景/道具 三类；其他类型（音效/音乐/特效等）整行丢弃
                if (parts.Count >= 2 && !IsAllowedAssetType(parts[1])) continue;
                // 资产名（首列）+ 命名建议（末列）都作为匹配关键字；先做弱清洗（未命名字样剥离、情节状态后缀剥离）
                var name = CleanupAssetName(parts[0]);
                var alias = parts.Count >= 4 ? CleanupAssetName(parts[^1]) : "";
                if (name.Length == 0 && alias.Length == 0) continue;
                if (name.Length > 0 && !assets.Contains(name)) assets.Add(name);
                if (alias.Length > 0 && !assets.Contains(alias)) assets.Add(alias);
            }
            if (assets.Count > 60) assets = assets.Take(60).ToList();
            // 归一化去重：C01-A_xxx 与 C01_A_xxx 视为同一资产，只保留首次出现的写法
            return DedupAssets(assets);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"资产需求提取失败：{ex.Message}", "Novel", ex);
            return new List<string>();
        }
    }

    /// <summary>资产类型白名单：只允许 角色/场景/道具（兼容中文/英文与「资产」后缀表述）；类型列为空时不拦截（按名称语义兜底）。</summary>
    private static bool IsAllowedAssetType(string type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return true;
        return t.Contains("角色") || t.Contains("人物") || t.Contains("场景") || t.Contains("道具")
            || t.Contains("character") || t.Contains("scene") || t.Contains("prop");
    }

    /// <summary>「未命名/无名」字样清洗：剥离该词及紧邻的「角色/人物/配角」等占位词；剥离后为空则整项丢弃（LLM 未按关系命名时至少不把「未命名」暴露给用户）。</summary>
    private static readonly Regex UnnamedWordRegex = new(@"未命?名(?:的)?(?:角色|人物|配角)?", RegexOptions.Compiled);

    /// <summary>角色情节状态后缀匹配：C01-哭泣 / C01_微笑 这类「基础资产名 + 状态」写法。</summary>
    private static readonly Regex CharacterStateSuffixRegex = new(@"^(?<base>C\d+)[-_](?<state>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>情节状态词（表情/情绪/动作/伤势等，弱兜底用）：角色资产名若以这些词作状态后缀，剥离后缀保留基础资产名；
    /// 服饰/发型词（旗袍、晚礼服、常服、战斗服等）不在此列，不会误伤合法变体。</summary>
    private static readonly HashSet<string> PlotStateWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // 表情/情绪
        "哭", "泣", "哭泣", "抽泣", "哭喊", "哭诉", "落泪", "流泪", "泪", "笑", "微笑", "微笑中", "大笑", "苦笑", "假笑",
        "怒", "生气", "发怒", "暴怒", "愤怒", "怒视", "怒目", "皱眉", "闭眼", "瞪眼", "严肃", "平静",
        "惊讶", "惊恐", "惊慌", "害怕", "恐惧", "紧张", "尴尬", "伤心", "难过", "痛苦", "悲痛", "开心", "高兴", "兴奋", "沮丧", "疲惫", "困倦",
        // 动作/伤势/状态
        "受伤", "流血", "带伤", "重伤", "轻伤", "包扎", "受伤后", "带伤时", "战斗", "战斗中", "打斗", "奔跑",
        "跪", "跪地", "坐", "坐下", "站", "站立", "倒地", "晕倒", "昏迷", "变身", "变身中", "爬行", "跳跃", "躲藏",
        "大喊", "怒吼", "怒吼中", "低声",
    };

    /// <summary>资产名弱清洗：①剥离「未命名」字样；②角色资产若以情节状态词作后缀（C01-哭泣）则剥离后缀保留基础名。
    /// 合法服饰/发型变体（C01-A、C01-B）不受影响。</summary>
    private static string CleanupAssetName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = UnnamedWordRegex.Replace(raw.Trim(), "").Trim().Trim('_', '-', ' ', '　');
        if (t.Length == 0) return "";
        var m = CharacterStateSuffixRegex.Match(t);
        if (m.Success && PlotStateWords.Contains(m.Groups["state"].Value))
            return m.Groups["base"].Value;
        return t;
    }

    /// <summary>资产关键字归一化：段号连接符 -/_ 视为等价（C01-A ≡ C01_A），用于去重与匹配。</summary>
    private static string AssetKey(string s) => (s ?? "").Trim().Replace('_', '-');

    /// <summary>按归一化键去重，保留首次出现的原始写法；忽略空白项；并合并前缀变体（C01-A 与 C01-A_墨绿旗袍 视为同一资产，保留基础名）。</summary>
    private static List<string> DedupAssets(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var it in items)
        {
            var t = (it ?? "").Trim();
            if (t.Length == 0) continue;
            if (seen.Add(AssetKey(t))) outList.Add(t);
        }
        // 前缀变体合并：仅当基础键本身含段号时（如 C01-A 与 C01-A_墨绿旗袍），后者视为前者变体、仅保留基础名；
        // 无段号的基础资产（C01）与段号变体（C01-A）是不同资产，不得互相吞并，保证服饰/发型变体不丢失
        var ordered = outList.OrderBy(AssetKey, StringComparer.OrdinalIgnoreCase).ToList();
        var baseKeys = new List<string>();
        var final = new List<string>();
        foreach (var it in ordered)
        {
            var key = AssetKey(it);
            if (baseKeys.Any(b => b.Contains('-') && key.StartsWith(b + "-", StringComparison.OrdinalIgnoreCase)))
                continue;
            baseKeys.Add(key);
            final.Add(it);
        }
        return final;
    }

    private static List<string> ComputeGaps(List<string> confirmed, List<string> matchedFiles)
    {
        var gaps = new List<string>();
        var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in matchedFiles)
            matchedNames.Add(Path.GetFileNameWithoutExtension(f));
        foreach (var a in confirmed)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (!matchedNames.Any(n => n.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(n, StringComparison.OrdinalIgnoreCase)))
                gaps.Add(a);
        }
        return gaps;
    }

    /// <summary>语音缺口：角色圣经角色（每人一个音色）与音频文件模糊匹配，未匹配 = 音色缺口。</summary>
    private static List<string> ComputeVoiceGaps(List<string> voices, List<string> audioFiles)
    {
        var gaps = new List<string>();
        var audioNames = audioFiles.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        foreach (var v in voices)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (!audioNames.Any(n => n.Contains(v, StringComparison.OrdinalIgnoreCase) || v.Contains(n, StringComparison.OrdinalIgnoreCase)))
                gaps.Add(v);
        }
        return gaps;
    }

    /// <summary>从角色圣经提取语音角色清单（每人一个音色资产）；角色圣经为空时返回空。</summary>
    private async Task<List<string>> ExtractVoicesWithLlmAsync(NovelPipelineContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.Characters)) return new List<string>();
        try
        {
            var system = """
                你是影视语音资产规划助手。给定「角色圣经」，列出所有需要配音的角色的角色名（含状态 ID 则一并保留，如「C02 林晚」）。
                规则：每人一行，只输出角色名列表，不输出解释、不输出对白、不输出多人对话。
                """ + "\n" + NovelPromptEngineering.LanguageDirective(ctx.Options.OutputLanguage);
            var user = "【角色圣经】\n" + Truncate(ctx.Characters, 4000);
            var raw = await CallChatAsync(system, user, 1024, ctx.Options, ct);
            var voices = new List<string>();
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim().TrimStart('-', '*', '•', ' ', '\t');
                if (t.Length == 0 || t.StartsWith("【") || t.StartsWith("#") || t.StartsWith("```")) continue;
                if (!voices.Contains(t)) voices.Add(t);
            }
            var deduped = DedupAssets(voices);
            if (deduped.Count > 40) deduped = deduped.Take(40).ToList();
            return deduped;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"语音资产提取失败：{ex.Message}", "Novel", ex);
            return new List<string>();
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

    private static string BuildAssetScanText(AssetScanResult r, bool dirOk)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【资产扫描结果】");
        sb.AppendLine();
        sb.AppendLine("已确认资产（角色/场景/道具）：");
        sb.AppendLine(r.ConfirmedAssets.Count > 0 ? string.Join("\n", r.ConfirmedAssets.Select(x => "- " + x)) : "- (无，请先确认分场/角色后重跑以提取资产需求)");
        sb.AppendLine();
        sb.AppendLine("资产目录：" + (dirOk ? "已扫描" : "未配置或不存在（未扫描）"));
        sb.AppendLine("资产目录枚举图片文件数：" + r.AvailableFiles.Count);
        sb.AppendLine("已匹配资产文件：" + (r.MatchedFiles.Count > 0 ? string.Join("\n", r.MatchedFiles.Select(x => "- " + x)) : "- (无)"));
        sb.AppendLine();
        sb.AppendLine("缺失资产（需补齐）：" + (r.Gaps.Count > 0 ? string.Join("；", r.Gaps) : "无"));
        sb.AppendLine();
        sb.AppendLine("语音资产（角色圣经角色，每人一个音色）：");
        sb.AppendLine(r.VoiceAssets.Count > 0 ? string.Join("\n", r.VoiceAssets.Select(x => "- " + x)) : "- (无角色圣经或未提取到角色)");
        sb.AppendLine("资产目录枚举音频文件数：" + r.AudioFiles.Count);
        sb.AppendLine("缺失音色（需在 VoiceStudio 生成）：" + (r.VoiceGaps.Count > 0 ? string.Join("；", r.VoiceGaps) : "无"));
        sb.AppendLine();
        if (r.ConfirmedAssets.Count == 0 && r.VoiceAssets.Count == 0)
            sb.AppendLine("提示：未提取到资产需求，无法核对缺失；请确认分场/角色后「重跑本阶段」以重新提取。");
        else if (!dirOk)
            sb.AppendLine("提示：未配置资产目录，已确认资产全部视为缺失；可直接点击「生成资产提示词」分批输出，补齐资产后再「重跑本阶段」重新扫描。");
        else if (r.Gaps.Count > 0 || r.VoiceGaps.Count > 0)
            sb.AppendLine("提示：检测到缺失资产，可用「生成缺口提示词」分批输出——图像缺口→ComfyUI 资产图提示词；音色缺口→VoiceStudio 音色卡。生成后复制使用，补齐后重新扫描。");
        else
            sb.AppendLine("未发现缺失，可继续下一步。");
        return sb.ToString();
    }

    private async Task<List<string>> ClassifyAssetsWithLlmAsync(List<string> unMatched, List<string> confirmed, NovelPromptOptions opts, CancellationToken ct)
    {
        var system = """
            你是资产归类助手。给定一份「已确认资产清单」和一批「文件名」，判断每个文件名语义上属于哪个已确认资产（角色/场景/道具）。
            规则：按需归到最贴切的一个已确认资产；确实无关的输出 "无关"。
            只输出每行「文件名 => 资产名或无关」，不要解释。
            """;
        var user = new StringBuilder();
        user.AppendLine("【已确认资产】").AppendLine(string.Join("\n", confirmed));
        user.AppendLine().AppendLine("【文件名】").AppendLine(string.Join("\n", unMatched));
        var raw = await CallChatAsync(system, user.ToString(), 2048, opts, ct);
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split('\n'))
        {
            var idx = line.IndexOf("=>");
            if (idx < 0) continue;
            var filePart = line.Substring(0, idx).Trim().Trim('"', '「', '」');
            var assetPart = line.Substring(idx + 2).Trim().Trim('"', '「', '」');
            if (assetPart == "无关") continue;
            var matched = unMatched.FirstOrDefault(u => Path.GetFileName(u).Contains(filePart, StringComparison.OrdinalIgnoreCase)
                                                        || filePart.Contains(Path.GetFileName(u), StringComparison.OrdinalIgnoreCase));
            if (matched != null) mapped.Add(matched);
        }
        return mapped.ToList();
    }

    // ============================================================
    // S5.5 缺口提示词分批生成（图像→ComfyUI 资产图提示词 / 音色→VoiceStudio 音色卡）
    // ============================================================
    /// <summary>一次只处理一批缺口（offset 起），图像缺口在前、音色缺口在后；每批单独一次 LLM 调用并约束输出量，避免超上下文。
    /// 图像缺口在入口先过滤：丢弃人物情节状态资产与衣物单品（只生成角色三视图[基础+不同衣服变体]/场景/道具提示词），进度按过滤后数量计。</summary>
    public async Task<GapPromptBatch> GenerateGapPromptsAsync(
        NovelPipelineContext ctx, IReadOnlyList<string> imageGaps, IReadOnlyList<string> voiceGaps,
        int offset, CancellationToken ct)
    {
        imageGaps = FilterGapAssets(imageGaps);
        var total = imageGaps.Count + voiceGaps.Count;
        if (total == 0 || offset < 0 || offset >= total)
            return new GapPromptBatch { Processed = offset, Total = total, HasMore = false };

        var isVoice = offset >= imageGaps.Count;
        var batchSize = isVoice ? Math.Max(1, ctx.Options.VoiceBatchSize) : Math.Max(1, ctx.Options.ImageBatchSize);
        var kindEnd = isVoice ? total : imageGaps.Count;
        var end = Math.Min(offset + batchSize, kindEnd);
        var slice = isVoice
            ? voiceGaps.Skip(offset - imageGaps.Count).Take(end - offset).ToList()
            : imageGaps.Skip(offset).Take(end - offset).ToList();

        var (system, user) = isVoice
            ? BuildVoicePromptBatch(ctx, slice)
            : BuildImagePromptBatch(ctx, slice, BuildSubsequentRefs(ctx, imageGaps, slice));

        // 主批：输出被长度截断时自动续写（finish_reason=length 追加续写，最多 GapPromptMaxContinue 轮）
        var raw = await CallChatCompletingAsync(system, user, StageMaxTokens, ctx.Options, ct, GapPromptMaxContinue);
        var prompts = ParseGapPromptLines(raw, slice);

        // 补漏：本批仍有资产未生成提示词时，针对缺失资产追加补充调用（最多 2 轮），杜绝「后面被截断」导致整批不完整
        for (var round = 0; round < 2 && prompts.Count < slice.Count; round++)
        {
            var missing = slice.Where(x => !prompts.Any(p => AssetKey(GapLineName(p)) == AssetKey(x))).ToList();
            if (missing.Count == 0) break;
            var (sys2, user2) = isVoice
                ? BuildVoicePromptBatch(ctx, missing)
                : BuildImagePromptBatch(ctx, missing, BuildSubsequentRefs(ctx, imageGaps, missing));
            user2 = "【仅补充以下缺失资产】\n" + string.Join("\n", missing.Select(x => "- " + x))
                    + "\n\n请只按原格式输出上述资产的提示词行（资产名：提示词），一条都不能少，不得输出已有内容。";
            var raw2 = await CallChatCompletingAsync(sys2, user2, StageMaxTokens, ctx.Options, ct, GapPromptMaxContinue);
            var extra = ParseGapPromptLines(raw2, missing);
            if (extra.Count == 0) break;
            prompts.AddRange(extra);
        }

        return new GapPromptBatch
        {
            Processed = end,
            Total = total,
            HasMore = end < total,
            Prompts = prompts,
        };
    }

    /// <summary>从「资产名：提示词」行提取资产名（兼容中文/英文冒号）。</summary>
    private static string GapLineName(string line)
    {
        var t = line.Trim();
        var idx = t.IndexOf('：');
        if (idx <= 0) idx = t.IndexOf(':');
        return idx > 0 ? t.Substring(0, idx).Trim() : t;
    }

    /// <summary>角色资产名匹配：以角色 ID 开头（如 C01、C01-A、C01-A_少年），用于识别角色资产与其所属角色。</summary>
    private static readonly Regex CharacterAssetRegex = new(
        @"^(C\d+)(?:[-_].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 同一角色的后续资产（需参考图保证人物一致性）：参考图来源必须是资产目录中**真实存在**的该角色资产图（同一人）。
    /// 仅当该角色已有资产图（文件名与该角色资产关键字模糊匹配）时，其后续服饰/发型变体才带参考图；
    /// 没有任何已存在资产图的角色，其全部资产提示词一律不带参考图（角色的第一个提示词绝不含参考图）。
    /// </summary>
    private static List<(string Asset, string BaseId, string Primary)> BuildSubsequentRefs(
        NovelPipelineContext ctx, IReadOnlyList<string> imageGaps, IReadOnlyList<string> slice)
    {
        var refs = new List<(string, string, string)>();
        if (imageGaps.Count == 0 || slice.Count == 0) return refs;

        // 资产目录中已存在的图片文件名（去扩展名）：用于判定"该角色是否已有真实资产图"
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = ctx.Options.AssetDirectory;
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (!ImageExts.Contains(Path.GetExtension(f))) continue;
                var n = Path.GetFileNameWithoutExtension(f);
                if (n.Length > 0) existingNames.Add(n);
            }
        }

        // 每个角色基础 ID → 已存在资产图中第一个可匹配的资产图名（参考图来源）；匹配不到记录空串（该角色无参考图）
        var firstOfChar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < imageGaps.Count; i++)
        {
            var name = (imageGaps[i] ?? "").Trim();
            var m = CharacterAssetRegex.Match(name);
            if (!m.Success) continue;
            var baseId = m.Groups[1].Value;
            if (firstOfChar.ContainsKey(baseId)) continue;
            var hit = existingNames.FirstOrDefault(e =>
                e.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(e, StringComparison.OrdinalIgnoreCase));
            firstOfChar[baseId] = hit ?? "";
        }

        foreach (var g in slice)
        {
            var name = (g ?? "").Trim();
            var m = CharacterAssetRegex.Match(name);
            if (!m.Success) continue;
            var baseId = m.Groups[1].Value;
            // 无真实存在的该角色资产图 → 不带参考图（第一个提示词绝不能有参考图）
            if (!firstOfChar.TryGetValue(baseId, out var primary) || string.IsNullOrEmpty(primary)) continue;
            // 该资产本身就是已存在的图 → 无需参考
            if (AssetKey(primary) == AssetKey(name)) continue;
            refs.Add((name, baseId, primary));
        }
        return refs;
    }

    /// <summary>衣物单品词（弱兜底用）：角色资产名若以这些词作后缀（如 C01_风衣、C02_短裙），视为单独衣物资产，不生成提示词；
    /// 整套服装描述词（晚礼服/常服/战斗服/校服/汉服/旗袍/婚纱等，通常是不同衣服变体）不在此列，不会误伤。</summary>
    private static readonly HashSet<string> ClothingItemWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "上衣", "下装", "外套", "风衣", "夹克", "衬衫", "T恤", "卫衣", "毛衣", "背心", "马甲", "西装",
        "长裙", "短裙", "连衣裙", "半身裙", "长裤", "短裤", "牛仔裤", "靴子", "皮鞋", "布鞋", "凉鞋", "运动鞋",
        "帽子", "鸭舌帽", "围巾", "领带", "领结", "腰带", "手套", "袜子", "披风", "斗篷", "内衣",
    };

    /// <summary>缺口资产过滤（生成提示词前）：只保留 角色基础/服饰发型变体、场景、道具；
    /// 丢弃 ①人物情节状态资产（C01-哭泣 → 不生成提示词）②衣物单品（C01_风衣 → 不单独生成）。
    /// 非角色资产（scene_/prop_/无角色 ID 前缀）一律保留。</summary>
    private static IReadOnlyList<string> FilterGapAssets(IReadOnlyList<string> imageGaps)
    {
        if (imageGaps == null || imageGaps.Count == 0) return Array.Empty<string>();
        var outList = new List<string>(imageGaps.Count);
        foreach (var g in imageGaps)
        {
            var name = (g ?? "").Trim();
            if (name.Length == 0) continue;
            var m = CharacterAssetRegex.Match(name);
            if (!m.Success) { outList.Add(name); continue; }
            var suffix = name.Substring(m.Groups[1].Value.Length).TrimStart('-', '_', ' ');
            if (suffix.Length == 0) { outList.Add(name); continue; }   // C01 → 基础三视图
            if (PlotStateWords.Contains(suffix)) continue;             // 情节状态 → 丢弃
            if (ClothingItemWords.Contains(suffix)) continue;          // 衣物单品 → 丢弃
            outList.Add(name);                                         // 段号变体/描述性资产 → 保留
        }
        return outList;
    }

    private static (string System, string User) BuildImagePromptBatch(
        NovelPipelineContext ctx, List<string> gaps, List<(string Asset, string BaseId, string Primary)> subsequentRefs)
    {
        var system = $"""
            你是 ComfyUI 资产图提示词生成助手。输入：角色圣经/世界观/分场摘要 + 本批缺失的资产名。
            任务：为每个缺失资产生成一条可直接用于 ComfyUI 文生图的资产图提示词。
            输出格式：每个资产一行「资产名：提示词」，资产名与输入完全一致，提示词与资产名之间用中文冒号「：」分隔；只输出资产行，不要解释、不要序号、不要代码块、行首不要任何列表符号。
            资产类型判定：资产名以角色 ID 开头（如 C01、C01-A、C02-B）或在角色圣经/小说中能找到对应人物的 → 角色资产；资产名含 scene_ 或「场景」字样 → 场景资产；含 prop_ 或「道具」字样 → 道具资产；其余按名称语义判断。
            【角色资产提示词要求（硬性规则）】
            1. 只描写该角色静态外貌：人物形象、五官、发型、发色、衣着、配饰、体态/身材、气质等，描写越详细越好；
            2. 必须输出人物三视图：正面、侧面、背面全身像（同一画面内并排展示），构图适合做资产参考卡；
            3. 严禁写入小说情节中的表情（如哭泣、大笑、愤怒、惊恐、悲伤等特定时刻的情绪）与动态神态，表情保持中性平静；严禁写入对白、动作、事件或剧情氛围；
            4. 若该角色有多个状态资产（C01-A/C01-B…），本条只描写本状态的外貌，不得混入其他状态特征；
            5. 若输入给出「参考图要求」，该资产提示词必须明确要求以指定参考图（该角色第一张资产图）为基准生成，锁定人物一致性（可使用 ControlNet / IPAdapter 等参考图控制方式），并只描述本资产相对第一资产的外貌差异点。
            【场景/道具资产提示词要求】主体外观与结构描述、材质、风格与媒介、构图（画幅 {ctx.Options.AspectRatio}）；禁止写入情节事件、人物与对白。
            【通用要求】
            1. 风格与媒介与项目整体一致（如 3D 数字渲染、写实动漫混合等）；
            2. 负面约束（如水印、变形、多余人、文字、低质量）；
            3. 独立性与排他：每条提示词只描述该行资产名对应的主体本身；禁止把同场其他角色、其他资产、情节对白或事件写进本条；不同资产行之间互不引用；
            4. 输出语言：提示词正文必须使用{NovelPromptEngineering.LangName(ctx.Options.OutputLanguage)}；资产名保持输入原名，禁止翻译或改写；
            5. 完整性：本批每个资产都必须生成一条提示词，一条都不能少；输出较长时宁可精简每条内容，也绝不允许截断或遗漏任一资产。
            """;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ctx.Characters))
            sb.AppendLine("【角色圣经（摘要）】\n" + Truncate(ctx.Characters, 4000)).AppendLine();
        if (!string.IsNullOrWhiteSpace(ctx.Worldbuilding))
            sb.AppendLine("【世界观（摘要）】\n" + Truncate(ctx.Worldbuilding, 2000)).AppendLine();
        if (!string.IsNullOrWhiteSpace(ctx.Treatment))
            sb.AppendLine("【分场大纲（摘要）】\n" + Truncate(ctx.Treatment, 2000)).AppendLine();
        sb.AppendLine($"【画幅】{ctx.Options.AspectRatio}");
        sb.AppendLine().AppendLine("【本批缺失资产】").AppendLine(string.Join("\n", gaps.Select(x => "- " + x)));
        if (subsequentRefs.Count > 0)
        {
            // 同一角色的后续资产：提示词必须带参考图要求（以第一资产图为基准锁定人物一致性）
            sb.AppendLine().AppendLine("【参考图要求】以下资产是同一角色的后续资产，其提示词必须包含「以参考图（角色第一张资产图）为基准生成，确保人物形象一致」的明确要求（可使用 ControlNet / IPAdapter 等参考图方式），并列出本资产相对第一资产的外貌差异点：");
            foreach (var r in subsequentRefs)
                sb.AppendLine($"- {r.Asset}（角色 {r.BaseId} 的后续资产，第一资产 {r.Primary} 已生成，参考图 = {r.Primary} 的资产图）");
        }
        sb.AppendLine().AppendLine("请按格式输出本批每个资产的提示词。");
        return (system, sb.ToString());
    }

    private static (string System, string User) BuildVoicePromptBatch(NovelPipelineContext ctx, List<string> voices)
    {
        var system = """
            你是 VoiceStudio 语音资产规划助手。输入：角色圣经 + 本批角色名。
            任务：为每个角色生成一张「音色卡」，供用户在 VoiceStudio 中克隆/设计该角色的语音。
            输出格式：每个角色一行「角色名：音色卡」，角色名与输入完全一致，用中文冒号「：」分隔；只输出角色行，不要解释、不要序号、不要代码块。
            音色卡要求（紧凑中文描述，3~5 行以内，每条 ≤200 字）：
            1. 性别与年龄段；
            2. 音色基调（可用气质类比，如温润清冷、低沉磁性）；
            3. 语速与节奏（如中偏慢、句间停顿稍长）；
            4. 情绪基线（如平静克制、尾音微沉）与说话习惯；
            5. 用途说明：全片该角色语音统一使用此音色。
            6. 输出语言：音色卡正文必须使用{NovelPromptEngineering.LangName(ctx.Options.OutputLanguage)}；角色名保持输入原名，禁止翻译或改写。
            7. 完整性：本批每个角色都必须生成一张音色卡，一条都不能少；输出较长时宁可精简，也绝不允许截断或遗漏任一角色。
            禁止：不得写对白台词，不得做多人对话。
            """;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ctx.Characters))
            sb.AppendLine("【角色圣经（摘要）】\n" + Truncate(ctx.Characters, 4000)).AppendLine();
        sb.AppendLine("【本批角色】").AppendLine(string.Join("\n", voices.Select(x => "- " + x)));
        sb.AppendLine().AppendLine("请按格式输出本批每个角色的音色卡。");
        return (system, sb.ToString());
    }

    /// <summary>解析模型输出行：按本批资产名校验 + 清理行首列表标记 + 归一化去重（C01-A/C01_A 变体只留首个），只保留「资产名：提示词」行。</summary>
    private static List<string> ParseGapPromptLines(string raw, IReadOnlyList<string> expectedAssets)
    {
        var expected = new HashSet<string>(expectedAssets.Select(AssetKey), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim().TrimStart('-', '*', '•', ' ', '\t');
            if (t.Length == 0 || t.StartsWith("```") || t.StartsWith("【") || t.StartsWith("#")) continue;
            var idx = t.IndexOf('：');
            if (idx <= 0) idx = t.IndexOf(':');
            if (idx <= 0) continue; // 非「资产名：提示词」行（如解释/前言）直接丢弃
            var name = AssetKey(t.Substring(0, idx).Trim());
            if (!expected.Contains(name)) continue; // 输出行资产名不在本批中 → 丢弃
            if (!seen.Add(name)) continue;          // 同一资产（含连接符变体）只保留首个
            outList.Add(t);
        }
        return outList;
    }

    // ============================================================
    // S6 镜头规划门
    // ============================================================
    public async Task<ShotPlanResult> PlanShotsAsync(NovelPipelineContext ctx, CancellationToken ct, Action<NovelBatchProgress>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(ctx.Treatment))
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["NovelTreatmentRequired"] ?? "请先完成并确认分场表（S4）");

        var system = NovelPromptEngineering.BuildStageSystem(NovelStage.ShotPlanning, ctx.Options);
        var scenes = SplitScenes(ctx.Treatment);
        var plan = new ShotPlanResult();
        var rawSb = new StringBuilder();
        var totalParsed = 0;
        var failedScenes = 0;
        var sw = Stopwatch.StartNew();
        LogService.Instance.Info($"镜头规划开始：解析到 {scenes.Count} 个场次，分批上限 {ShotPlanBatchSize} 镜/批，每批上限 {ShotPlanMaxTokens} token", "Novel");

        for (var i = 0; i < scenes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (sceneId, sceneText) = scenes[i];
            LogService.Instance.Info($"镜头规划：场次 {sceneId} 开始", "Novel");
            onProgress?.Invoke(new NovelBatchProgress
            {
                Text = "",
                Done = i,
                Total = scenes.Count,
                ProgressText = $"场次 {sceneId} 生成中",
            });

            // 单场生成：短场单次（截断续写），长场/疑似截断转分批，代码拼装该场完整镜头表
            var (shots, sceneRaw) = await PlanSceneShotsAsync(ctx, system, sceneId, sceneText, totalParsed + 1, ct,
                onProgress, i, scenes.Count);
            rawSb.AppendLine(sceneRaw.TrimEnd()).AppendLine();
            foreach (var s in shots)
            {
                if (string.IsNullOrEmpty(s.SourceScene)) s.SourceScene = sceneId;
                plan.Shots.Add(s);
            }
            totalParsed += shots.Count;
            if (shots.Count == 0) failedScenes++;
            onProgress?.Invoke(new NovelBatchProgress
            {
                Text = "",
                Done = i + 1,
                Total = scenes.Count,
                ProgressText = $"场次 {sceneId} 完成（{shots.Count} 镜）",
            });
            LogService.Instance.Info($"镜头规划：场次 {sceneId} 解析 {shots.Count} 个镜头", "Novel");
        }

        plan.RawText = rawSb.ToString().TrimEnd();
        sw.Stop();
        LogService.Instance.Info(
            $"镜头规划完成：{scenes.Count} 场，成功解析 {totalParsed} 个镜头，失败场次 {failedScenes}，耗时 {sw.Elapsed.TotalSeconds:0.0}s",
            "Novel");
        return plan;
    }

    /// <summary>
    /// 单场镜头生成：短场先单次调用（被长度截断时自动续写）；分场文本超长或输出疑似截断时转分批生成
    /// （≤ShotPlanBatchSize 镜/批，CONTINUE/DONE 协议推进），最后在代码里拼装该场完整镜头表，杜绝输出不完整导致后续无法继续执行。
    /// </summary>
    private async Task<(List<ShotItem> Shots, string Raw)> PlanSceneShotsAsync(
        NovelPipelineContext ctx, string system, string sceneId, string sceneText, int startNumber, CancellationToken ct,
        Action<NovelBatchProgress>? onProgress = null, int sceneIdx = 0, int sceneCount = 1)
    {
        // 超长分场直接分批，避免必现的截断白白消耗一次调用
        if (sceneText.Length <= LongSceneCharThreshold)
        {
            var user = BuildShotPlanUser(ctx, sceneId, sceneText, startNumber);
            var raw = await CallChatCompletingAsync(system, user, ShotPlanMaxTokens, ctx.Options, ct);
            var shots = TryParseShotJson(raw);
            if (shots.Count > 0)
            {
                var section = "## 场次 " + sceneId + "\n" + raw.Trim();
                onProgress?.Invoke(new NovelBatchProgress
                {
                    Text = section,
                    Done = sceneIdx + 1,
                    Total = sceneCount,
                    ProgressText = $"场次 {sceneId} 完成（{shots.Count} 镜）",
                });
                return (shots, section);
            }
            if (TryParsePartialShots(raw).Count > 0)
            {
                // JSON 解析失败但救回了部分镜头对象 → 输出疑似截断，转为分批重生成完整镜头表
                LogService.Instance.Warn($"镜头规划：场次 {sceneId} 输出疑似截断，转为分批生成", "Novel");
            }
            else
            {
                var fallback = TryParseShotFallback(raw);
                if (fallback.Count > 0)
                {
                    var section = "## 场次 " + sceneId + "\n" + raw.Trim();
                    onProgress?.Invoke(new NovelBatchProgress
                    {
                        Text = section,
                        Done = sceneIdx + 1,
                        Total = sceneCount,
                        ProgressText = $"场次 {sceneId} 完成（{fallback.Count} 镜）",
                    });
                    return (fallback, section);
                }
            }
        }
        return await PlanSceneShotsBatchedAsync(ctx, system, sceneId, sceneText, startNumber, ct, onProgress, sceneIdx, sceneCount);
    }

    /// <summary>分批生成单场镜头表：每批 ≤ShotPlanBatchSize 镜，按 CONTINUE 标记推进（满批默认还有下一批，宁多一轮也绝不丢镜头），全部批次拼装为该场完整镜头表。</summary>
    private async Task<(List<ShotItem> Shots, string Raw)> PlanSceneShotsBatchedAsync(
        NovelPipelineContext ctx, string system, string sceneId, string sceneText, int startNumber, CancellationToken ct,
        Action<NovelBatchProgress>? onProgress = null, int sceneIdx = 0, int sceneCount = 1)
    {
        var shots = new List<ShotItem>();
        var rawSb = new StringBuilder();
        var header = $"## 场次 {sceneId}（分批生成）";
        rawSb.AppendLine(header);
        onProgress?.Invoke(new NovelBatchProgress
        {
            Text = header,
            Done = sceneIdx,
            Total = sceneCount,
            ProgressText = $"场次 {sceneId} · 分批生成中",
        });
        var number = startNumber;
        // 镜头总数预估：作为分批锚点。估算范围内保留「满批默认还有下一批」防漏镜头；超出估算后转入最终确认轮（严格推进），防镜头表无限膨胀。
        var estimate = await EstimateSceneShotCountAsync(ctx, system, sceneId, sceneText, ct);
        var normalBatches = EstimateNormalBatches(estimate);
        if (estimate is > 0)
            LogService.Instance.Info($"镜头规划：场次 {sceneId} 预估 {estimate.Value} 镜 → 估算内 {normalBatches} 批，超出后进入最终确认轮", "Novel");
        else
            LogService.Instance.Warn($"镜头规划：场次 {sceneId} 镜头总数预估失败，回退默认 {ShotPlanMaxBatches} 批上限", "Novel");

        for (var batch = 0; batch < ShotPlanMaxBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            var first = batch * ShotPlanBatchSize + 1;
            var last = first + ShotPlanBatchSize - 1;
            var overEstimate = estimate is > 0 && batch >= normalBatches;

            // 本批生成：先正常请求一次；若解析 0 个镜头，降级重试一次（提示继续输出或确无剩余输出空数组收尾），
            // 防止模型误判「已输出完」导致整场只保留前几批、后续镜头全部丢失。
            var raw = await CallChatCompletingAsync(system,
                BuildShotPlanUser(ctx, sceneId, sceneText, number)
                + "\n" + (overEstimate
                    ? NovelPromptEngineering.BuildShotPlanFinalCheckInstruction(number, shots, estimate!.Value)
                    : NovelPromptEngineering.BuildShotPlanBatchInstruction(first, last, batch + 1, number, shots)),
                ShotPlanMaxTokens, ctx.Options, ct);
            var parsed = TryParseShotJson(raw);
            if (parsed.Count == 0) parsed = TryParsePartialShots(raw);   // 截断兜底：救回完整镜头对象
            if (parsed.Count == 0)
            {
                LogService.Instance.Warn($"镜头规划：场次 {sceneId} 第 {batch + 1} 批未产出镜头，降级重试一次", "Novel");
                var retryRaw = await CallChatCompletingAsync(system,
                    BuildShotPlanUser(ctx, sceneId, sceneText, number)
                    + "\n" + (overEstimate
                        ? NovelPromptEngineering.BuildShotPlanFinalCheckInstruction(number, shots, estimate!.Value)
                        : NovelPromptEngineering.BuildShotPlanBatchInstruction(first, last, batch + 1, number, shots, retry: true)),
                    ShotPlanMaxTokens, ctx.Options, ct);
                var retryParsed = TryParseShotJson(retryRaw);
                if (retryParsed.Count == 0) retryParsed = TryParsePartialShots(retryRaw);
                if (retryParsed.Count == 0)
                {
                    // 连续两轮未产出：此前已有镜头则视为正常收尾（场景确已输出完），否则记录整场失败
                    LogService.Instance.Warn($"镜头规划：场次 {sceneId} 第 {batch + 1} 批连续两次未产出镜头，结束该场分批（已累计 {shots.Count} 镜）", "Novel");
                    break;
                }
                raw = retryRaw;
                parsed = retryParsed;
            }

            var added = 0;
            foreach (var s in parsed)
            {
                // 内容级去重：跨批重复镜头（模型批次漂移重排前文）按规范化签名相似度剔除，杜绝镜头表虚增
                if (IsDuplicateShot(shots, s))
                {
                    LogService.Instance.Warn($"镜头规划：场次 {sceneId} 第 {batch + 1} 批丢弃重复镜头（与已有镜头内容相似度过高）", "Novel");
                    continue;
                }
                if (string.IsNullOrEmpty(s.SourceScene)) s.SourceScene = sceneId;
                // 分批路径不信任模型 ID：按全局续号强制重编号，杜绝重复/跳号导致后续镜头拼接错乱
                s.ShotId = $"SH{number:000}";
                shots.Add(s);
                number++;
                added++;
            }
            var batchSection = $"\n—— 批 {batch + 1} ——\n{raw.Trim()}";
            rawSb.AppendLine($"—— 批 {batch + 1} ——");
            rawSb.AppendLine(raw.Trim());
            // 本批完成后立即回传，UI 实时追加本批镜头表
            onProgress?.Invoke(new NovelBatchProgress
            {
                Text = batchSection,
                Done = sceneIdx,
                Total = sceneCount,
                ProgressText = $"场次 {sceneId} · 第 {batch + 1} 批（累计 {shots.Count} 镜）",
            });
            // 推进协议：只认「单独一行」的 CONTINUE/DONE（防对白/动作中的 continue 字样误触发）；
            // 估算范围内满批默认还有下一批（宁多一轮也绝不丢镜头），超出估算后只看显式 CONTINUE（防膨胀）
            var hasMore = HasLineFlag(raw, "CONTINUE")
                          || (added >= ShotPlanBatchSize && !overEstimate);
            if (!hasMore) break;
        }
        if (shots.Count == 0)
            LogService.Instance.Warn($"镜头规划：场次 {sceneId} 分批生成未解析出任何镜头", "Novel");
        return (shots, rawSb.ToString());
    }

    /// <summary>镜头总数预估：一次小调用让模型核算该场大概镜头数（单镜 4–15 秒），作为分批锚点。</summary>
    private async Task<int?> EstimateSceneShotCountAsync(NovelPipelineContext ctx, string system, string sceneId, string sceneText, CancellationToken ct)
    {
        try
        {
            var user = BuildShotPlanUser(ctx, sceneId, sceneText, 1)
                + "\n\n" + NovelPromptEngineering.BuildShotPlanCountEstimateInstruction();
            var raw = await CallChatCompletingAsync(system, user, ShotPlanEstimateMaxTokens, ctx.Options, ct);
            return TryParseShotCount(raw);
        }
        catch
        {
            return null; // 预估失败不阻塞分批：回退默认批次上限
        }
    }

    /// <summary>从预估输出中解析镜头总数（取首个整数，clamp 1..999）。</summary>
    private static int? TryParseShotCount(string? raw)
    {
        var m = Regex.Match(raw ?? "", @"\d+");
        if (!m.Success) return null;
        if (!int.TryParse(m.Value, out var n) || n <= 0) return null;
        return Math.Clamp(n, 1, 999);
    }

    /// <summary>估算内的正常批次数：ceil(总数/每批)，预估失败时回退全局上限。</summary>
    private static int EstimateNormalBatches(int? estimate)
    {
        if (estimate is > 0) return Math.Max(1, (int)Math.Ceiling(estimate.Value / (double)ShotPlanBatchSize));
        return ShotPlanMaxBatches;
    }

    /// <summary>推进协议严格匹配：标记必须独占一行（忽略空白），防止对白/动作中的 continue 字样误触发续批。</summary>
    private static bool HasLineFlag(string raw, string flag)
    {
        foreach (var line in (raw ?? "").Split('\n'))
        {
            if (string.Equals(line.Trim(), flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>镜头内容签名：首帧 + 动作 + 对白 + 目的 + 空间 + 景别（比较用规范化）。</summary>
    private static string BuildShotSignature(ShotItem s) =>
        string.Join("|", new[] { s.FirstFrame, s.Action, s.Dialogue, s.Purpose, s.WorldPosition, s.ShotSize });

    /// <summary>比较规范化：去空白/标点/符号并小写，规避措辞差异。</summary>
    private static string NormalizeForCompare(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex.Replace(text.Normalize(), @"[\s\p{P}\p{S}]+", "").ToLowerInvariant();
    }

    /// <summary>Levenshtein 相似度（0..1）：用于跨批镜头内容去重判定。</summary>
    private static double SimilarityRatio(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return 1.0 - dp[a.Length, b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    /// <summary>内容级去重：候选镜头与已生成镜头规范化签名相似度 ≥ 阈值即视为重复（模型批次漂移重排前文时剔除）。</summary>
    private static bool IsDuplicateShot(IReadOnlyList<ShotItem> existing, ShotItem candidate)
    {
        var cand = NormalizeForCompare(BuildShotSignature(candidate));
        if (cand.Length == 0) return false;
        foreach (var e in existing)
        {
            var eSig = NormalizeForCompare(BuildShotSignature(e));
            if (eSig.Length == 0) continue;
            if (SimilarityRatio(cand, eSig) >= ShotDuplicateThreshold) return true;
        }
        return false;
    }

    /// <summary>把分场表文本按场次切分（识别行首 S01 / 场次X / 第X场），解析失败则整段当一场。</summary>
    private static List<(string Id, string Text)> SplitScenes(string treatment)
    {
        var list = new List<(string, string)>();
        var lines = treatment.Split('\n');
        var curId = "S01";
        var cur = new StringBuilder();
        var sceneLine = new Regex(@"^\s*(?:S\d{1,3}|场次\s*[一二三四五六七八九十\d]+|第\s*[一二三四五六七八九十\d]+\s*场)", RegexOptions.IgnoreCase);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (sceneLine.IsMatch(t))
            {
                if (cur.Length > 0) list.Add((curId, cur.ToString()));
                var idMatch = Regex.Match(t, @"S\d{1,3}", RegexOptions.IgnoreCase);
                curId = idMatch.Success ? idMatch.Value : $"S{list.Count + 1:00}";
                cur = new StringBuilder();
                cur.AppendLine(t);
            }
            else
            {
                cur.AppendLine(line);
            }
        }
        if (cur.Length > 0) list.Add((curId, cur.ToString()));
        if (list.Count == 0) list.Add(("S01", treatment));
        return list;
    }

    /// <summary>逐场镜头规划 user：只带该场分场 + 角色圣经 + 已确认资产（不整本小说，控制上下文与输出量）。</summary>
    private static string BuildShotPlanUser(NovelPipelineContext ctx, string sceneId, string sceneText, int startNumber)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【已确认：角色圣经】").AppendLine(string.IsNullOrWhiteSpace(ctx.Characters) ? "(无)" : ctx.Characters.Trim()).AppendLine();
        if (ctx.ConfirmedAssets.Count > 0)
            sb.AppendLine("【已确认资产】\n" + string.Join("\n", ctx.ConfirmedAssets.Select(x => "- " + x)) + "\n");
        sb.AppendLine($"【本场：{sceneId}】");
        sb.AppendLine(sceneText.Trim());
        sb.AppendLine();
        sb.AppendLine($"本场镜头 ID 从 SH{startNumber:000} 开始连续编号（SH{startNumber:000}、SH{startNumber + 1:000}…），不得跳号、重复或改用其他编号。");
        sb.AppendLine("字段名保持英文；字段值（目的/动作/对白/描述等）使用所选输出语言；对白保持用户确认的原文。");
        sb.AppendLine("请先做该场时长核算，再输出该场镜头清单 JSON 数组（字段与前面要求一致）。");
        return sb.ToString();
    }

    private static readonly Regex JsonFenceRegex = new(@"```(?:json)?\s*(\[[\s\S]*?\])\s*```", RegexOptions.IgnoreCase);

    private static List<ShotItem> TryParseShotJson(string raw)
    {
        try
        {
            var json = raw.Trim();
            var m = JsonFenceRegex.Match(json);
            if (m.Success) json = m.Groups[1].Value;
            else
            {
                var s = json.IndexOf('[');
                var e = json.LastIndexOf(']');
                if (s >= 0 && e > s) json = json.Substring(s, e - s + 1);
            }
            var dto = JsonSerializer.Deserialize<List<ShotJsonDto>>(json);
            if (dto == null) return new List<ShotItem>();
            return dto.Select(MapShot).ToList();
        }
        catch
        {
            return new List<ShotItem>();
        }
    }

    /// <summary>截断/非法 JSON 兜底：用正则抽取所有完整的镜头对象（{"shotId":"SH…",…}），救回已生成部分。</summary>
    private static readonly Regex ShotObjectRegex = new(@"\{[^{}]*""shotId""[^{}]*\}", RegexOptions.IgnoreCase);

    private static List<ShotItem> TryParsePartialShots(string raw)
    {
        var list = new List<ShotItem>();
        try
        {
            foreach (Match m in ShotObjectRegex.Matches(raw))
            {
                try
                {
                    var d = JsonSerializer.Deserialize<ShotJsonDto>(m.Value);
                    if (d != null) list.Add(MapShot(d));
                }
                catch { /* 单个对象损坏则跳过 */ }
            }
        }
        catch { }
        return list;
    }

    private static ShotItem MapShot(ShotJsonDto d) => new()
    {
        ShotId = d.shotId ?? "",
        SourceScene = d.sourceScene ?? "",
        Purpose = d.purpose ?? "",
        Duration = d.duration.GetValueOrDefault(),
        ShotSize = d.shotSize ?? "",
        CameraPosition = d.cameraPosition ?? "",
        FocalLength = d.focalLength ?? "",
        CameraMovement = d.cameraMovement ?? "",
        WorldPosition = d.worldPosition ?? "",
        ScreenPosition = d.screenPosition ?? "",
        Gaze = d.gaze ?? "",
        AxisSide = d.axisSide ?? "",
        Action = d.action ?? "",
        Performance = d.performance ?? "",
        PropsState = d.propsState ?? "",
        Dialogue = d.dialogue ?? "",
        DialogueCharCount = d.dialogueCharCount.GetValueOrDefault(),
        DialogueSeconds = d.dialogueSeconds.GetValueOrDefault(),
        AssetsUsed = d.assetsUsed ?? "",
        FirstFrame = d.firstFrame ?? "",
        LastFrame = d.lastFrame ?? "",
        ContinuityRisk = d.continuityRisk ?? "",
        LongDurationReason = d.longDurationReason ?? "",
    };

    private static List<ShotItem> TryParseShotFallback(string raw)
    {
        // 降级：按镜头行拆（`SH001` / `镜头` / `- S` 起始的行），各字段尽量取行内关键字
        var list = new List<ShotItem>();
        var lines = raw.Split('\n');
        ShotItem? cur = null;
        foreach (var line in lines)
        {
            var t = line.Trim();
            var m = Regex.Match(t, @"^(?:[-*\s]*)(SH\d+|镜头\s*\d+)");
            if (m.Success)
            {
                cur = new ShotItem { ShotId = m.Groups[1].Value };
                list.Add(cur);
            }
            else if (cur != null && t.Length > 0)
            {
                cur.Action += " " + t;
            }
        }
        return list;
    }

    // ============================================================
    // S7 逐镜提示词
    // ============================================================
    public async Task<IReadOnlyList<PromptResult>> GeneratePromptsAsync(NovelPipelineContext ctx, CancellationToken ct, Action<NovelBatchProgress>? onProgress = null)
    {
        if (ctx.ShotPlan == null)
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["NovelShotPlanMissing"] ?? "请先完成并确认镜头规划（S6）");
        if (ctx.ShotPlan.Shots.Count == 0)
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["NovelShotPlanEmpty"] ?? "镜头规划已确认但未解析出任何镜头，请返回 S6 修正镜头表或重跑");

        var results = new List<PromptResult>();
        var shots = ctx.ShotPlan.Shots;
        var totalShots = shots.Count;
        var done = 0;
        var sw = Stopwatch.StartNew();
        LogService.Instance.Info($"逐镜提示词开始：共 {totalShots} 个镜头，格式 {ctx.Options.OutputFormat}，粒度 {(ctx.Options.Granularity == NovelGranularity.PerSceneMerged ? "逐场合并" : "逐镜头")}", "Novel");

        // 连续剧情（导演台）：按场次分组 → 每场拆成 ≤ContinuousStoryMaxSegments 镜一组 → 每组一次「公共设定 + N 组提示词组」调用（各组即镜头，代码拼装杜绝截断）；单镜场次回退六段式通用提示词
        if (ctx.Options.OutputFormat == NovelOutputFormat.H3DirectorStory)
        {
            var maxSeg = MiniMaxScenarios.ContinuousStoryMaxSegments;
            foreach (var g in shots.GroupBy(s => s.SourceScene))
            {
                var groupShots = g.ToList();
                var totalChunks = (groupShots.Count + maxSeg - 1) / maxSeg;
                for (var c = 0; c < groupShots.Count; c += maxSeg)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = groupShots.Skip(c).Take(maxSeg).ToList();
                    var chunkNo = c / maxSeg + 1;
                    string? prevHandoff = c > 0 ? groupShots[c - 1].LastFrame : null;
                    PromptResult result;
                    if (chunk.Count == 1)
                    {
                        // 单镜场次无法构成「公共设定 + 多组提示词组」，回退为该镜的六段式通用提示词（仍是 MiniMax H3 格式；开关开启且无资产图时降级三字段 T2VA）
                        var shot = chunk[0];
                        var media = ResolveShotRefImages(ctx, shot);
                        string system, user;
                        if (ctx.Options.EnableNoRefT2VA && media.Count == 0)
                        {
                            system = H3T2VAPromptBuilder.BuildSystem(ctx, shot, ctx.Options.AspectRatio, ctx.Options.OutputLanguage);
                            user = H3T2VAPromptBuilder.BuildUser(BuildH3ShotShortText(ctx, shot, null), ctx.Options.OutputLanguage);
                        }
                        else
                        {
                            system = MiniMaxAssembler.BuildSystem(H3FullRefScenario, FullRefForm(ctx), LangKey(ctx.Options.OutputLanguage), media);
                            user = MiniMaxAssembler.BuildUser(H3FullRefScenario, FullRefForm(ctx), BuildH3ShotShortText(ctx, shot, null), media, LangKey(ctx.Options.OutputLanguage));
                        }
                        var text = await CallChatCompletingAsync(system, user, ShotMaxTokens, ctx.Options, ct);
                        result = new PromptResult
                        {
                            ShotId = shot.ShotId,
                            SourceScene = shot.SourceScene,
                            Model = ModelLabel(ctx.Options.OutputFormat),
                            Prompt = text,
                        };
                    }
                    else
                    {
                        // c+1 = 本批第一个提示词组的全场连续编号：=1 首批输出公共设定，后续批只续写提示词组（公共设定仅一次、保证完整）
                        var (system, user) = BuildH3DirectorMessages(ctx, g.Key, chunk, prevHandoff, c + 1);
                        var text = await CallChatCompletingAsync(system, user, ShotMaxTokens, ctx.Options, ct);
                        result = new PromptResult
                        {
                            ShotId = totalChunks > 1 ? $"{g.Key}·组{chunkNo}/{totalChunks}" : g.Key,
                            SourceScene = g.Key,
                            Model = ModelLabel(ctx.Options.OutputFormat),
                            Prompt = text,
                        };
                    }
                    results.Add(result);
                    done += chunk.Count;
                    onProgress?.Invoke(new NovelBatchProgress
                    {
                        Text = FormatPromptChunk(result),
                        Done = done,
                        Total = totalShots,
                        ProgressText = $"场次 {g.Key} · 导演台 {chunkNo}/{totalChunks}（{done}/{totalShots} 镜）",
                    });
                    LogService.Instance.Info($"逐镜提示词：场次 {g.Key} 导演台组 {chunkNo}/{totalChunks} 完成（累计 {done}/{totalShots} 镜）", "Novel");
                }
            }
        }
        else if (ctx.Options.Granularity == NovelGranularity.PerSceneMerged)
        {
            var groups = shots.GroupBy(s => s.SourceScene);
            foreach (var g in groups)
            {
                var groupShots = g.ToList();
                var totalBatches = (groupShots.Count + MergedSubBatchSize - 1) / MergedSubBatchSize;
                for (var b = 0; b < totalBatches; b++)
                {
                    ct.ThrowIfCancellationRequested();
                    var sub = groupShots.Skip(b * MergedSubBatchSize).Take(MergedSubBatchSize).ToList();
                    var subMedia = ResolveBatchRefImages(ctx, sub);
                    var system = BuildShotSystem(ctx, sub[0], sub.Count > 1, subMedia);
                    var user = BuildMergedUser(ctx, g.Key, sub, b + 1, totalBatches, subMedia);
                    var text = await CallChatCompletingAsync(system, user, ShotMaxTokens, ctx.Options, ct);
                    var result = new PromptResult
                    {
                        ShotId = totalBatches > 1 ? $"{g.Key}·批{b + 1}/{totalBatches}" : g.Key,
                        SourceScene = g.Key,
                        Model = ModelLabel(ctx.Options.OutputFormat),
                        Prompt = text,
                    };
                    results.Add(result);
                    done += sub.Count;
                    // 本批完成后立即回传，UI 实时追加本批合并提示词
                    onProgress?.Invoke(new NovelBatchProgress
                    {
                        Text = FormatPromptChunk(result),
                        Done = done,
                        Total = totalShots,
                        ProgressText = $"场次 {g.Key} · 批 {b + 1}/{totalBatches}（{done}/{totalShots} 镜）",
                    });
                    LogService.Instance.Info($"逐镜提示词：场次 {g.Key} 批 {b + 1}/{totalBatches} 完成（累计 {done}/{totalShots} 镜）", "Novel");
                }
            }
        }
        else
        {
            // 逐镜头：每条独立调用（每条 ≤ ShotMaxTokens）；输出被长度截断时自动续写至完整
            for (var i = 0; i < shots.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var shot = shots[i];
                var prev = i > 0 ? shots[i - 1] : null;
                var media = ResolveShotRefImages(ctx, shot);
                var system = BuildShotSystem(ctx, shot, false, media);
                var user = BuildShotUser(ctx, shot, prev, i, media);
                var text = await CallChatCompletingAsync(system, user, ShotMaxTokens, ctx.Options, ct);
                var result = new PromptResult
                {
                    ShotId = shot.ShotId,
                    SourceScene = shot.SourceScene,
                    Model = ModelLabel(ctx.Options.OutputFormat),
                    Prompt = text,
                };
                results.Add(result);
                done++;
                // 本镜完成后立即回传，UI 实时追加本条提示词
                onProgress?.Invoke(new NovelBatchProgress
                {
                    Text = FormatPromptChunk(result),
                    Done = done,
                    Total = totalShots,
                    ProgressText = $"镜头 {done}/{totalShots}",
                });
                LogService.Instance.Info($"逐镜提示词：{i + 1}/{totalShots}（{shot.ShotId}）", "Novel");
            }
        }
        sw.Stop();
        LogService.Instance.Info($"逐镜提示词完成：生成 {results.Count} 条，耗时 {sw.Elapsed.TotalSeconds:0.0}s", "Novel");
        return results;
    }

    /// <summary>单条提示词展示块（与 VM 最终拼装格式一致：## 镜头ID（场次）· 模型）。</summary>
    private static string FormatPromptChunk(PromptResult r) =>
        $"## {r.ShotId}（{r.SourceScene}）· {r.Model}\n{r.Prompt}\n\n";

    private static string ModelLabel(NovelOutputFormat f) => f switch
    {
        NovelOutputFormat.Seedance20 => "Seedance 2.0",
        NovelOutputFormat.H3DirectorStory => "MiniMax H3·导演台",
        _ => "MiniMax H3",
    };

    private static string BuildShotSystem(NovelPipelineContext ctx, ShotItem shot, bool merged, IReadOnlyList<string>? media = null)
    {
        return ctx.Options.OutputFormat switch
        {
            NovelOutputFormat.Seedance20 => SeedancePromptBlocks.BuildSystem(shot, ctx.Options.AspectRatio, ctx.Options.OutputLanguage),
            NovelOutputFormat.H3FullReference => ctx.Options.EnableNoRefT2VA && (media is null || media.Count == 0)
                ? H3T2VAPromptBuilder.BuildSystem(ctx, shot, ctx.Options.AspectRatio, ctx.Options.OutputLanguage)
                : MiniMaxAssembler.BuildSystem(H3FullRefScenario, FullRefForm(ctx), LangKey(ctx.Options.OutputLanguage), media),
            _ => H3PromptBuilder.BuildSystem(shot, ctx.Options.AspectRatio, ctx.Options.OutputLanguage),
        };
    }

    private static string BuildShotUser(NovelPipelineContext ctx, ShotItem shot, ShotItem? prev, int index, IReadOnlyList<string>? media = null)
    {
        if (ctx.Options.OutputFormat == NovelOutputFormat.H3FullReference)
            return ctx.Options.EnableNoRefT2VA && (media is null || media.Count == 0)
                ? H3T2VAPromptBuilder.BuildUser(BuildH3ShotShortText(ctx, shot, prev), ctx.Options.OutputLanguage)
                : MiniMaxAssembler.BuildUser(H3FullRefScenario, FullRefForm(ctx), BuildH3ShotShortText(ctx, shot, prev), media, LangKey(ctx.Options.OutputLanguage));

        var sb = new StringBuilder();
        sb.AppendLine("【已确认：角色圣经】").AppendLine(string.IsNullOrWhiteSpace(ctx.Characters) ? "(无)" : ctx.Characters.Trim()).AppendLine();
        if (ctx.ConfirmedAssets.Count > 0)
            sb.AppendLine("【已确认资产】\n" + string.Join("\n", ctx.ConfirmedAssets.Select(x => "- " + x)) + "\n");
        sb.AppendLine("【本镜头（单镜头）】");
        AppendShot(sb, shot);
        if (prev != null)
        {
            sb.AppendLine();
            sb.AppendLine("【上一镜头尾帧状态（用于连续性，提示词中必须展开为完整物理描述，不得用“继续/同上”指代）】");
            AppendShot(sb, prev, label: "prev");
        }
        sb.AppendLine();
        sb.AppendLine("请输出该镜头的视频提示词。");
        return sb.ToString();
    }

    private static string BuildMergedUser(NovelPipelineContext ctx, string scene, List<ShotItem> sceneShots, int batchNo = 1, int totalBatches = 1, IReadOnlyList<string>? media = null)
    {
        if (ctx.Options.OutputFormat == NovelOutputFormat.H3FullReference)
            return ctx.Options.EnableNoRefT2VA && (media is null || media.Count == 0)
                ? H3T2VAPromptBuilder.BuildUser(BuildH3MergedShortText(ctx, scene, sceneShots, batchNo, totalBatches), ctx.Options.OutputLanguage)
                : MiniMaxAssembler.BuildUser(H3FullRefScenario, FullRefForm(ctx), BuildH3MergedShortText(ctx, scene, sceneShots, batchNo, totalBatches), media, LangKey(ctx.Options.OutputLanguage));

        var sb = new StringBuilder();
        sb.AppendLine("【已确认：角色圣经】").AppendLine(string.IsNullOrWhiteSpace(ctx.Characters) ? "(无)" : ctx.Characters.Trim()).AppendLine();
        var title = totalBatches > 1
            ? $"【本场（{scene}）合并预览 · 第 {batchNo}/{totalBatches} 批（本批 {sceneShots.Count} 个镜头）】"
            : $"【本场（{scene}）合并预览，{sceneShots.Count} 个镜头】";
        sb.AppendLine(title);
        var idx = 1;
        foreach (var s in sceneShots)
        {
            sb.AppendLine($"—— 镜头 {idx} ——");
            AppendShot(sb, s);
            idx++;
        }
        sb.AppendLine();
        if (totalBatches > 1)
            sb.AppendLine("本场镜头较多，已分批：本批只合并本批列出的镜头，镜头编号从上面列出的首个镜头开始连续；必须完整覆盖本批全部镜头。");
        sb.AppendLine("请为这一场输出一条合并的宽松预览提示词（作为快速预览，不要求逐镜独立；仍须给出清晰的首帧与时间轴）。");
        return sb.ToString();
    }

    private static void AppendShot(StringBuilder sb, ShotItem s, string? label = null)
    {
        var pre = label == "prev" ? "（上一镜头）" : "";
        sb.AppendLine($"{s.ShotId}{pre}");
        sb.AppendLine($"来源场次：{s.SourceScene}");
        sb.AppendLine($"目的：{s.Purpose}");
        sb.AppendLine($"时长：{s.Duration:0.#} 秒；景别：{s.ShotSize}；机位：{s.CameraPosition}；焦段：{s.FocalLength}；运镜：{s.CameraMovement}");
        sb.AppendLine($"世界位置：{s.WorldPosition}；画面位置：{s.ScreenPosition}；视线：{s.Gaze}；轴线：{s.AxisSide}");
        sb.AppendLine($"动作：{s.Action}");
        sb.AppendLine($"表演节拍：{s.Performance}");
        sb.AppendLine($"道具状态：{s.PropsState}");
        sb.AppendLine($"对白：{s.Dialogue}（字数 {s.DialogueCharCount}，预计 {s.DialogueSeconds:0.#} 秒）");
        sb.AppendLine($"资产：{s.AssetsUsed}");
        sb.AppendLine($"首帧硬描述：{s.FirstFrame}");
        sb.AppendLine($"尾帧硬描述：{s.LastFrame}");
        sb.AppendLine($"连续性风险：{s.ContinuityRisk}");
        if (!string.IsNullOrWhiteSpace(s.LongDurationReason)) sb.AppendLine($"长时长理由：{s.LongDurationReason}");
    }

    // ============================================================
    // MiniMax 六段式通用提示词 / 连续剧情（导演台）组装（复用扩写页 MiniMaxAssembler，保证格式一致）
    // ============================================================
    /// <summary>扩写页 full_reference 场景（Minimax六段式通用提示词）。</summary>
    private static readonly MiniMaxScenario H3FullRefScenario =
        MiniMaxScenarios.GetScenarioByPeId("pe_expand_h3_full_reference")
        ?? throw new InvalidOperationException("内置 MiniMax full_reference 场景缺失（场景目录异常）");

    /// <summary>扩写页 continuous_story 场景（连续剧情（导演台））。</summary>
    private static readonly MiniMaxScenario H3DirectorScenario =
        MiniMaxScenarios.GetScenarioByPeId("pe_expand_minimax_continuous_story")
        ?? throw new InvalidOperationException("内置 MiniMax continuous_story 场景缺失（场景目录异常）");

    /// <summary>输出语言 → MiniMaxAssembler 语言键（zh/en）。</summary>
    private static string LangKey(NovelOutputLanguage lang) => lang == NovelOutputLanguage.English ? "en" : "zh";

    /// <summary>六段式表单：时长写需求里（逐镜时长精确到 0.1s，不用 5/10/15 档），严格改写不编造情节。</summary>
    private static Dictionary<string, string> FullRefForm(NovelPipelineContext ctx) => new()
    {
        ["duration_seconds"] = "custom",
        ["aspect_ratio"] = ctx.Options.AspectRatio,
        ["expand_mode"] = "strict",
    };

    /// <summary>六段式/导演台短文本：角色圣经 + 已确认资产 + 本镜头（+上一镜头尾帧状态）。</summary>
    private static string BuildH3ShotShortText(NovelPipelineContext ctx, ShotItem shot, ShotItem? prev)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【已确认：角色圣经】").AppendLine(string.IsNullOrWhiteSpace(ctx.Characters) ? "(无)" : ctx.Characters.Trim()).AppendLine();
        if (ctx.ConfirmedAssets.Count > 0)
            sb.AppendLine("【已确认资产】\n" + string.Join("\n", ctx.ConfirmedAssets.Select(x => "- " + x)) + "\n");
        sb.AppendLine("【本镜头（单镜头）】");
        AppendShot(sb, shot);
        if (prev != null)
        {
            sb.AppendLine();
            sb.AppendLine("【上一镜头尾帧状态（用于连续性，提示词中必须展开为完整物理描述，不得用“继续/同上”指代）】");
            AppendShot(sb, prev, label: "prev");
        }
        return sb.ToString().Trim();
    }

    /// <summary>六段式逐场合并短文本（覆盖本批全部镜头，时间轴连续）。</summary>
    private static string BuildH3MergedShortText(NovelPipelineContext ctx, string scene, List<ShotItem> sceneShots, int batchNo, int totalBatches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【已确认：角色圣经】").AppendLine(string.IsNullOrWhiteSpace(ctx.Characters) ? "(无)" : ctx.Characters.Trim()).AppendLine();
        if (ctx.ConfirmedAssets.Count > 0)
            sb.AppendLine("【已确认资产】\n" + string.Join("\n", ctx.ConfirmedAssets.Select(x => "- " + x)) + "\n");
        var title = totalBatches > 1
            ? $"【本场（{scene}）合并预览 · 第 {batchNo}/{totalBatches} 批（本批 {sceneShots.Count} 个镜头）】"
            : $"【本场（{scene}）合并预览，{sceneShots.Count} 个镜头】";
        sb.AppendLine(title);
        var idx = 1;
        foreach (var s in sceneShots)
        {
            sb.AppendLine($"—— 镜头 {idx} ——");
            AppendShot(sb, s);
            idx++;
        }
        if (totalBatches > 1)
            sb.AppendLine("本场镜头较多，已分批：本批只合并本批列出的镜头，镜头编号从上面列出的首个镜头开始连续；必须完整覆盖本批全部镜头。");
        return sb.ToString().Trim();
    }

    /// <summary>连续剧情（导演台）：公共设定 + N 组提示词组（plan_mode=custom，每组 = 一个已规划镜头；跨批传入上一批末段停帧保证衔接）。
    /// segmentStart 为本批第一个提示词组的全场连续编号：=1 表示首批（输出完整公共设定）；&gt;1 表示后续批次（公共设定已在首批输出，
    /// 本批禁止重复输出公共设定，只续写提示词组且编号按全场连续，杜绝公共参数重复/不完整）。</summary>
    private static (string System, string User) BuildH3DirectorMessages(
        NovelPipelineContext ctx, string scene, List<ShotItem> shots, string? prevHandoff, int segmentStart = 1)
    {
        var n = Math.Min(shots.Count, MiniMaxScenarios.ContinuousStoryMaxSegments);
        var media = ResolveBatchRefImages(ctx, shots);       // 本批镜头在资产目录中真实存在的资产图 → 作 <Picture N> 参考标签
        var sec = PickSegmentSeconds(shots);                 // 段时长参考档位（仅参考，模型自行按剧情拆分）
        var form = new Dictionary<string, string>
        {
            ["prompt_kind"] = "r2v",          // 小说流水线 S7 以资产目录真实资产图作参考 → 参考图锁角色（有图版导演台）
            ["plan_mode"] = "custom",         // 镜头已规划：每组即一个镜头
            ["segment_count"] = n.ToString(),
            ["segment_seconds"] = sec,
            ["aspect_ratio"] = ctx.Options.AspectRatio,
            ["expand_mode"] = "expand",       // 允许补全运镜/声画细节（导演台推荐）
        };
        for (var i = 0; i < n; i++)
        {
            var beat = new StringBuilder();
            AppendShot(beat, shots[i]);
            form[$"segment_{i + 1}_beat"] = beat.ToString().Trim();
        }
        var lang = LangKey(ctx.Options.OutputLanguage);
        var sb = new StringBuilder();
        if (segmentStart > 1)
            sb.AppendLine("【批次说明】本批是场次 " + scene + " 的后续批次：公共设定已在第一批完整输出，本批仅续写提示词组，不要重复公共设定/主体定义。").AppendLine();
        sb.AppendLine("【已确认：角色圣经】").AppendLine(string.IsNullOrWhiteSpace(ctx.Characters) ? "(无)" : ctx.Characters.Trim()).AppendLine();
        if (ctx.ConfirmedAssets.Count > 0)
            sb.AppendLine("【已确认资产】\n" + string.Join("\n", ctx.ConfirmedAssets.Select(x => "- " + x)) + "\n");
        sb.AppendLine($"【本场创作需求】场次 {scene}：共 {shots.Count} 个镜头，已按计划逐段填入「第 N 段在干什么」。每段时长参考约 {sec} 秒（档位仅供参考，不必精确），模型按剧情实际自行拆分，生成时长落在参考范围附近即可。");
        if (!string.IsNullOrWhiteSpace(prevHandoff))
            sb.AppendLine($"【承接上一批】上一批末段停在：{prevHandoff}。本批第 1 段必须「无硬切。紧接上一段。」，配乐主题延续。");

        var system = MiniMaxAssembler.BuildSystem(H3DirectorScenario, form, lang, media);
        if (segmentStart > 1)
        {
            // 后续批次：覆盖上方「第一部分：公共设定」的要求，只续写提示词组；编号按全场连续
            var endSeg = segmentStart + n - 1;
            system += "\n【本批补充·最高优先级】（覆盖上方『第一部分：公共设定』的要求，本批不适用）" +
                      $"本批为同一场次的后续批次，公共设定已在第一批完整输出。本批禁止再输出「公共设定」「公共参数」「主体定义」及任何角色外貌/服装/场景描述；" +
                      $"直接从「===== 提示词组 {segmentStart} =====」开始输出本批 {n} 组提示词组（每组仍按六段式且禁止主体定义），" +
                      $"提示词组编号按全场连续（本批为第 {segmentStart} 至第 {endSeg} 组），不得从 1 重新编号；" +
                      "只引用第一批公共设定中已有的 <Subject N> 编号与身份，禁止编造新主体。角色圣经仅作主体引用参照，不要重复定义。" +
                      $"\n【段时长参考·说明】每段时长档位（约 {sec} 秒）仅作参考，不必精确等于该值；按剧情实际自行拆分，生成时长落在参考范围附近即可。";
        }
        else
        {
            // 首批：公共设定必须一次给全且完整，供后续批次引用
            system += "\n【公共设定完整性·最高优先级】公共设定（主体定义）必须完整：列出本场所有出场主体（角色/场景），" +
                      "每个主体给出与角色圣经一致且完整的身份、外貌与服装描述，禁止省略、简化或仅写编号。" +
                      $"\n【段时长参考·说明】每段时长档位（约 {sec} 秒）仅作参考，不必精确等于该值；按剧情实际自行拆分，生成时长落在参考范围附近即可。";
        }
        return (
            system,
            MiniMaxAssembler.BuildUser(H3DirectorScenario, form, sb.ToString().Trim(), media, lang));
    }

    /// <summary>导演台每段时长：按本组镜头平均时长就近取 5/10/15 秒档（仅供模型参考，非硬约束）。</summary>
    private static string PickSegmentSeconds(List<ShotItem> shots)
    {
        if (shots.Count == 0) return "5";
        var avg = shots.Average(s => s.Duration);
        var rounded = (int)Math.Round(avg / 5.0) * 5;
        return Math.Clamp(rounded, 5, 15).ToString();
    }

    /// <summary>本镜头参考图：从 AssetsUsed 解析资产名（顿号/逗号/分号/空白分隔），在资产目录中按文件名模糊匹配真实存在的资产图
    /// （优先精确匹配文件名），返回绝对路径列表（同一图只取一次）。供六段式/导演台作 &lt;Picture N&gt; 参考标签锁角色。</summary>
    private static List<string> ResolveShotRefImages(NovelPipelineContext ctx, ShotItem shot)
    {
        var outList = new List<string>();
        var dir = ctx.Options.AssetDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || shot is null) return outList;
        var names = (shot.AssetsUsed ?? "")
            .Split(new[] { '，', ',', '、', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return outList;
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(f => ImageExts.Contains(Path.GetExtension(f))).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var exact = files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
            var hit = exact;
            if (hit is null)
            {
                hit = files.FirstOrDefault(f =>
                {
                    var bn = Path.GetFileNameWithoutExtension(f);
                    return bn.Contains(name, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(bn, StringComparison.OrdinalIgnoreCase);
                });
            }
            if (hit is { } h && used.Add(h)) outList.Add(h);
        }
        return outList;
    }

    /// <summary>整批镜头参考图并集（按出现顺序去重）。</summary>
    private static List<string> ResolveBatchRefImages(NovelPipelineContext ctx, IReadOnlyList<ShotItem> shots)
    {
        var outList = new List<string>();
        if (shots is null || shots.Count == 0) return outList;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in shots)
        {
            foreach (var p in ResolveShotRefImages(ctx, s))
            {
                if (seen.Add(p)) outList.Add(p);
            }
        }
        return outList;
    }

    /// <summary>
    /// 知识库检索（v2 深度解析）：目录枚举 + 关键词过滤，命中范围为「文件名 或 解析后的文档内容」。
    /// 内容命中会触发 .docx/.pdf（含扫描件 OCR）解析，解析结果按文件缓存复用。
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchKnowledgeBaseAsync(string kbPath, string keyword, CancellationToken ct)
    {
        var outList = new List<string>();
        if (string.IsNullOrWhiteSpace(kbPath) || !Directory.Exists(kbPath)) return outList;
        var kw = (keyword ?? "").Trim();
        try
        {
            foreach (var f in Directory.EnumerateFiles(kbPath, "*", SearchOption.TopDirectoryOnly))
            {
                // 知识库只认文本/可解析文档（.md/.txt/.docx/.pdf），不含资产图片
                if (!KnowledgeExts.Contains(Path.GetExtension(f))) continue;
                if (string.IsNullOrEmpty(kw) || Path.GetFileName(f).Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    outList.Add(f);
                    continue;
                }
                if (_docReader == null) continue;
                var text = await _docReader.ExtractTextAsync(f, ct);
                if (!string.IsNullOrEmpty(text) && text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    outList.Add(f);
            }
        }
        catch { }
        return outList;
    }

    // ============================================================
    // 通用 LLM 调用（优先用页面所选模型；未选则回落扩写默认模型）
    // ============================================================
    /// <summary>单次 LLM 调用（无续写）。</summary>
    private async Task<string> CallChatAsync(string system, string user, int maxTokens, NovelPromptOptions opts, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = system },
            new() { Role = "user", Content = user },
        };
        return (await CallChatFullAsync(messages, maxTokens, opts, ct)).Text;
    }

    /// <summary>带续写的 LLM 调用：finish_reason=length 时自动追加「从断点继续」直到完整（最多 maxContinues 轮），杜绝输出被截断导致后续无法执行。</summary>
    private async Task<string> CallChatCompletingAsync(string system, string user, int maxTokens, NovelPromptOptions opts, CancellationToken ct, int maxContinues = 2)
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = system },
            new() { Role = "user", Content = user },
        };
        var assembled = "";
        for (var i = 0; i <= maxContinues; i++)
        {
            var (chunk, reason) = await CallChatFullAsync(messages, maxTokens, opts, ct);
            assembled = i == 0 ? chunk : ExpandService.MergeContinuedText(assembled, chunk);
            if (!ExpandService.IsLengthFinish(reason) || i >= maxContinues) break;
            messages.Add(new ChatMessage { Role = "assistant", Content = assembled });
            messages.Add(new ChatMessage { Role = "user", Content = "输出因长度限制被截断。请紧接着最后一个字符继续输出剩余内容，不要重复已写内容，不要寒暄，不要解释。" });
        }
        return assembled;
    }

    private async Task<(string Text, string? FinishReason)> CallChatFullAsync(List<ChatMessage> messages, int maxTokens, NovelPromptOptions opts, CancellationToken ct)
    {
        var (provider, model) = await ResolveModelAsync(opts, ct);

        var body = new ChatCompletionRequest
        {
            Model = model.ModelName,
            Temperature = 0.7,
            TopP = 0.9,
            MaxTokens = maxTokens,
            Messages = { },
        };
        foreach (var m in messages) body.Messages.Add(new ChatMessage { Role = m.Role, Content = m.Content });
        ThinkingModelHeuristic.ApplyThinkingOff(body);

        var resp = await OpenAiHttpHelper.ChatAsync(provider.BaseUrl, provider.ApiKey, body, 180000, ct);
        if (resp.Choices.Count == 0)
            throw new InferencesException(InferenceErrorKind.NetworkError, "模型返回为空");
        var content = resp.Choices[0].Message?.Content?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(content))
            throw new InferencesException(InferenceErrorKind.NetworkError, "模型返回为空");
        return (ExpandService.StripThinkingTags(content), resp.Choices[0].FinishReason);
    }

    /// <summary>解析模型：页面已选且可解析 → 用之；否则回落扩写默认模型。</summary>
    private async Task<(ProviderConfig, ProviderModel)> ResolveModelAsync(NovelPromptOptions opts, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(opts.ProviderId) && !string.IsNullOrWhiteSpace(opts.ModelName))
        {
            try
            {
                var all = await _providerSvc.GetAllAsync(ct);
                var p = all.FirstOrDefault(x => x.Id == opts.ProviderId);
                var m = p?.Models.FirstOrDefault(x => x.ModelName == opts.ModelName);
                if (p != null && m != null) return (p, m);
            }
            catch { /* 解析失败则回落默认 */ }
        }
        var pick = await _providerSvc.GetDefaultExpandAsync(ct);
        if (pick == null)
        {
            throw new InferencesException(InferenceErrorKind.NoSuchProvider,
                Localizer.Instance?["NoExpandModelConfigured"] ?? "");
        }
        return pick.Value;
    }

    private sealed class ShotJsonDto
    {
        public string? shotId { get; set; }
        public string? sourceScene { get; set; }
        public string? purpose { get; set; }
        public double? duration { get; set; }
        public string? shotSize { get; set; }
        public string? cameraPosition { get; set; }
        public string? focalLength { get; set; }
        public string? cameraMovement { get; set; }
        public string? worldPosition { get; set; }
        public string? screenPosition { get; set; }
        public string? gaze { get; set; }
        public string? axisSide { get; set; }
        public string? action { get; set; }
        public string? performance { get; set; }
        public string? propsState { get; set; }
        public string? dialogue { get; set; }
        public int? dialogueCharCount { get; set; }
        public double? dialogueSeconds { get; set; }
        public string? assetsUsed { get; set; }
        public string? firstFrame { get; set; }
        public string? lastFrame { get; set; }
        public string? continuityRisk { get; set; }
        public string? longDurationReason { get; set; }
    }
}
