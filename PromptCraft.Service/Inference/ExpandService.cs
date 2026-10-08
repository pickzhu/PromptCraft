using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference.Minimax;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 提示词扩写（T2.1，对齐 prompt_master.js expandTextByLlm 在线链路 + expandOutputContinue.js）。
/// 参数：temperature 0.7 / top_p 0.9 / max_tokens 按篇幅（含自定义字数）；
/// 断点续写（finish_reason=length，12-480 字重叠去重 + 寒暄前缀清理，最多 3 次）；
/// 输出前完整清洗（think 块 / 思考：行 / GLM 特殊 token / &lt;answer&gt;）+ 质量词前缀。
/// </summary>
public sealed class ExpandService : IExpandService
{
    private readonly IServiceProvider _services;
    private readonly IProviderService _providerSvc;

    public ExpandService(IServiceProvider services)
    {
        _services = services;
        _providerSvc = services.GetRequiredService<IProviderService>();
    }

    public Task<string> ExpandAsync(ExpandRequest req, CancellationToken ct)
        => ExpandAsync(req, null, null, ct);

    public async Task<string> ExpandAsync(ExpandRequest req, ProviderConfig? provider, ProviderModel? model, CancellationToken ct)
    {
        var shortText = (req.ShortText ?? "").Trim();
        if (shortText.Length == 0)
        {
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["ExpandInputRequired"] ?? "");
        }

        if (provider == null || model == null)
        {
            var pick = await _providerSvc.GetDefaultExpandAsync(ct);
            if (pick == null)
            {
                throw new InferencesException(InferenceErrorKind.NoSuchProvider,
                    Localizer.Instance?["NoExpandModelConfigured"] ?? "");
            }
            (provider, model) = pick.Value;
        }

        // ---- 1. 提示词工程解析（对齐 _resolveExpandPromptsFromInfo） ----
        var outputLang = string.IsNullOrEmpty(req.OutputLang) ? "zh" : req.OutputLang;
        var lengthKey = string.IsNullOrEmpty(req.LengthKey) ? "medium" : req.LengthKey!;
        var peId = ExpandPromptEngineering.MapLegacyExpandPeId(req.PeId ?? req.RuleId);
        var mediaExpandMode = ExpandPromptEngineering.IsMinimaxProfile(peId);

        var prompts = ExpandPromptEngineering.ResolveExpand(peId, new ExpandPromptEngineering.ExpandResolveParams(
            ShortText: shortText,
            OutputLang: outputLang,
            ExpandLen: lengthKey,
            ExpandLenChars: req.LengthChars,
            UserExtraPrompt: req.CustomPrompt ?? "",
            MediaPaths: req.MediaPaths,
            MinimaxForm: req.MinimaxForm));
        if (prompts == null)
        {
            // profile 不存在（如自定义 id）：fallback 到 8 条基础规则（_resolveExpandPromptsFromInfo 的 ruleId 分支）
            var ruleId = Regex.Replace(peId, "^pe_", "");
            var system = ExpandRules.ResolveExpandSystemMessage(ruleId, "", outputLang, lengthKey, req.LengthChars, req.CustomPrompt ?? "");
            var user = ExpandRules.BuildUserPrompt(shortText, lengthKey, outputLang, req.LengthChars, ruleId, req.CustomPrompt ?? "");
            prompts = new ExpandPromptEngineering.ExpandPromptResult(
                system, user,
                ExpandRules.ResolveExpandMaxTokens(lengthKey, req.LengthChars),
                ruleId);
        }

        // ---- 2. 语言提示（langHint，对齐 expandTextByLlm:2361-2383；媒体型有专属文案） ----
        string langHint;
        if (mediaExpandMode)
        {
            langHint =
                "Media expand: output ONLY the finished prompt package. No questionnaires or confirmation forms. No chain-of-thought.";
            if (outputLang == "zh")
            {
                langHint +=
                    " 【输出语言锁定：中文】章节标题与正文都必须简体中文；六段标题用：主体定义:/摘要:/保留分析:/详细描述:/整体声景:/非叙事配乐:，禁止 subject_definitions: 等英文章节标题。仅 <Picture N>、<Subject N>、[Shot N]、fully_preserved 等技术标记可保留英文。";
            }
            else if (outputLang == "en")
            {
                langHint +=
                    " [Output language lock: English] Write section titles and prompt body in English.";
            }
        }
        else if (outputLang == "zh")
        {
            langHint =
                "【最高优先级】用户指定输出语言为中文。无论输入是中文、英文还是 tag（如 1girl、solo），扩写结果必须全部用中文（汉字）书写；禁止整段英文或英文 tag 列表。只输出扩写正文，不要思考过程或解释。";
        }
        else if (outputLang == "en")
        {
            langHint =
                "【Highest priority】User selected English output. Write the entire expanded prompt in English only, even if input is Chinese. No chain-of-thought or explanation.";
        }
        else
        {
            var isChinese = Regex.IsMatch(shortText, "[\u4e00-\u9fff]");
            langHint = isChinese
                ? "请用中文回答。请直接输出扩写结果，不要包含思考过程或解释。"
                : "Please answer in English. Output only the expanded prompt, no chain-of-thought.";
        }
        var systemMerged = $"{langHint}\n\n{prompts.System}";

        // ---- 3. 组装请求（对齐 expandTextByLlm generate：temperature 0.7 / top_p 0.9） ----
        // 媒体型扩写：
        //   zhipu + 纯图片 → 视觉预读后切换文本模型写作（对齐 2297-2310/2391-2424）
        //   其它 → 多模态组装（zhipu 视频内联/抽帧；非 zhipu 抽帧；音频/文件 path-only）
        object? userContent = prompts.User;
        var hasVideoMedia = false;
        if (mediaExpandMode && req.MediaPaths is { Count: > 0 })
        {
            var isZhipu = IsZhipuProvider(provider.Name, provider.BaseUrl);
            if (isZhipu && ZhipuMediaVision.MediaExpandImageOnly(req.MediaPaths))
            {
                var visionNotes = await ZhipuMediaVision.DescribeMediaForExpandAsync(
                    provider.BaseUrl, provider.ApiKey, req.MediaPaths, outputLang, model.ModelName, ct);
                if (!string.IsNullOrEmpty(visionNotes))
                {
                    // 预读成功：描述注入 user 文本，模型切换为文本模型（对齐 2404-2409）
                    userContent = (prompts.User ?? "")
                        + "\n\n【参考素材视觉描述（已由视觉模型预读，写作时必须引用 <Picture N> 标签）】\n"
                        + visionNotes;
                    model = new ProviderModel { ModelName = ZhipuMediaVision.TextExpandModel(model.ModelName) };
                }
                else
                {
                    // 预读失败：回退多模态 + 视觉模型（对齐 2410-2423）
                    var mm = await BuildMediaExpandMultimodalUserAsync(prompts.User, req.MediaPaths, provider.Name, provider.BaseUrl);
                    if (mm.Mode == "openai") userContent = mm.Content;
                    hasVideoMedia = mm.HasVideoMedia;
                    model = new ProviderModel { ModelName = ZhipuMediaVision.VisionFallbackModel(model.ModelName) };
                }
            }
            else
            {
                var mm = await BuildMediaExpandMultimodalUserAsync(prompts.User, req.MediaPaths, provider.Name, provider.BaseUrl);
                if (mm.Mode == "openai") userContent = mm.Content;
                hasVideoMedia = mm.HasVideoMedia;
            }
        }
        var body = new ChatCompletionRequest
        {
            Model = model.ModelName,
            Temperature = 0.7,
            TopP = 0.9,
            MaxTokens = ClampMaxTokens(model.ModelName, prompts.MaxTokens),
            Messages =
            {
                new ChatMessage { Role = "system", Content = systemMerged },
                new ChatMessage { Role = "user", Content = userContent },
            },
        };
        ThinkingModelHeuristic.ApplyThinkingOff(body);

        // ---- 4. 断点续写（对齐 continueExpandUntilComplete；媒体契约 full_reference / director_segments） ----
        var contract = ResolveExpandContract(peId, outputLang, req.MinimaxForm);
        var initialMessages = body.Messages.ToList();
        LogService.Instance.Info(string.Format(Localizer.Instance?["ExpandStartLogFormat"] ?? "", peId, provider.Name, model.ModelName, lengthKey) +
            (string.IsNullOrEmpty(req.LengthChars) ? "" : string.Format(Localizer.Instance?["ExpandStartCustomCharsSuffix"] ?? "", req.LengthChars)) +
            (mediaExpandMode ? (Localizer.Instance?["ExpandStartMediaSuffix"] ?? "") : ""), "Expand");
        var (text, _) = await ContinueExpandUntilCompleteAsync(
            initialMessages,
            contract,
            async (messages) =>
            {
                var req2 = new ChatCompletionRequest
                {
                    Model = model.ModelName,
                    Temperature = 0.7,
                    TopP = 0.9,
                    MaxTokens = ClampMaxTokens(model.ModelName, prompts.MaxTokens),
                    Messages = { },
                };
                req2.ExtraBody.Clear();
                foreach (var (k, v) in body.ExtraBody) req2.ExtraBody[k] = v;
                foreach (var m in messages) req2.Messages.Add(m);
                // Ollama 走原生 /api/chat + think:false（对齐 prompt_master.js 2471-2494；
                // 避免 Qwen3.5 等在 /v1/chat/completions 下 content 为空 → 「Local 返回为空」）
                //if (OllamaApiHelper.IsOllama(provider.Name, provider.BaseUrl))
                //{
                //    var ollama = await OllamaApiHelper.ChatAsync(
                //        provider!.BaseUrl, provider.ApiKey, model.ModelName, req2.Messages,
                //        temperature: 0.7, topP: 0.9, maxTokens: req2.MaxTokens ?? 0,
                //        hasVideoMedia ? 600000 : 300000, ct);
                //    if (!string.IsNullOrEmpty(ollama.Error))
                //    {
                //        return new ExpandChunk("", ollama.FinishReason, $"{provider.Name ?? ""}：{ollama.Error}");
                //    }
                //    return new ExpandChunk(ollama.Text, ollama.FinishReason, null);
                //}
                var resp = await OpenAiHttpHelper.ChatAsync(provider!.BaseUrl, provider.ApiKey, req2,
                    hasVideoMedia ? 300000 : 180000, ct);
                if (resp.Choices.Count == 0)
                {
                    return new ExpandChunk("", "", string.Format(Localizer.Instance?["ExpandEmptyResponseFormat"] ?? "", provider.Name ?? ""));
                }
                var choice = resp.Choices[0];
                var content = choice.Message?.Content?.ToString() ?? "";
                var reason = choice.FinishReason ?? "";
                if (string.IsNullOrWhiteSpace(content))
                {
                    return new ExpandChunk("", reason, string.Format(Localizer.Instance?["ExpandEmptyResponseFormat"] ?? "", provider.Name ?? ""));
                }
                return new ExpandChunk(content, reason, null);
            },
            StripThinkingTags,
            ct,
            onContinue: n => LogService.Instance.Info(string.Format(Localizer.Instance?["ExpandContinueLogFormat"] ?? "", n), "Expand"));
        LogService.Instance.Info(string.Format(Localizer.Instance?["ExpandDoneLogFormat"] ?? "", text.Length), "Expand");
        if (string.IsNullOrWhiteSpace(text))
            LogService.Instance.Warn(Localizer.Instance?["ExpandEmptyResultWarn"] ?? "", "Expand");

        // ---- 5. 结果（媒体 → normalizeMediaExpandOutput + 质量词前缀；非媒体 → 质量词前缀） ----
        if (mediaExpandMode)
        {
            text = ExpandMedia.NormalizeMediaExpandOutput(text);
        }
        return ApplyExpandQualityPrefix(text, req);
    }

    // ============================================================
    // 断点续写（expandOutputContinue.js continueExpandUntilComplete 等价）
    // ============================================================

    internal enum ExpandContractKind { None, Loose, FullReference, DirectorSegments }

    internal sealed class ExpandOutputContract
    {
        public ExpandContractKind Kind { get; init; } = ExpandContractKind.None;
        public string Lang { get; init; } = "zh";
        public int ExpectedGroups { get; init; }
    }

    internal sealed class ExpandChunk
    {
        public ExpandChunk(string text, string finishReason, string? error)
        {
            Text = text; FinishReason = finishReason; Error = error;
        }
        public string Text { get; }
        public string FinishReason { get; }
        public string? Error { get; }
    }

    internal const int ExpandContinueMax = 3;
    internal static string IncompleteExpandWarning =>
        Localizer.Instance?["ExpandContractIncomplete"] ?? "输出可能未写完，请检查各组字段是否齐全（摘要 / 保留分析 / 详细描述 / 整体声景 / 非叙事配乐）。";

    internal static bool IsLengthFinish(string? reason)
    {
        var r = (reason ?? "").Trim().ToLowerInvariant();
        return r == "length" || r == "max_tokens" || r == "max_token" || r == "limit";
    }

    /// <summary>mergeContinuedText：去寒暄前缀 + 12~480 字重叠去重。</summary>
    internal static string MergeContinuedText(string prev, string next)
    {
        var a = Regex.Replace(prev ?? "", "\\s+$", "");
        var b = Regex.Replace(next ?? "", "^\\s+", "");
        if (b.Length == 0) return a;
        if (a.Length == 0) return b;
        b = Regex.Replace(b, "^(?:好的[，,。.]|继续(?:输出|书写|写作)?[：:]|如下[：:])\\s*", "");
        if (b.StartsWith(a, StringComparison.Ordinal)) return b;
        const int minOverlap = 12;
        var max = Math.Min(a.Length, 480);
        for (var n = max; n >= minOverlap; n -= 1)
        {
            var tail = a[^n..];
            var idx = b.IndexOf(tail, StringComparison.Ordinal);
            if (idx == 0) return a + b[n..];
            if (idx > 0 && idx < 120) return a + b[(idx + n)..];
        }
        return a + (a.EndsWith('\n') ? "" : "\n") + b;
    }

    /// <summary>buildContinueUserMessage：续写 user 消息（对齐 buildContinueUserMessage，契约缺段版）。</summary>
    internal static string BuildContinueUserMessage(string partial, ExpandOutputContract contract, ExpandOutputInspect inspect)
    {
        var lang = contract.Lang == "en" ? "en" : "zh";
        var groupsFound = inspect?.GroupsFound ?? 0;
        var expected = contract.ExpectedGroups;
        if (lang == "en")
        {
            var extra = "Continue ONLY from the breakpoint. Do not repeat written text. No chat.";
            if (contract.Kind == ExpandContractKind.DirectorSegments)
            {
                extra += $" Need exactly {expected} prompt groups. Each group must contain in order: summary / retention_analysis / detailed_description / overall_soundscape / non_diegetic_music. Already have {groupsFound} group(s).";
            }
            else if (contract.Kind == ExpandContractKind.FullReference)
            {
                extra += " Finish the six sections: subject_definitions / summary / retention_analysis / detailed_description / overall_soundscape / non_diegetic_music.";
            }
            return extra + "\n\nAlready written (do not rewrite):\n-----\n" + partial + "\n-----\nContinue immediately after the last character.";
        }
        var zhExtra = "请从断点继续写，不要重复已写内容，不要寒暄。";
        if (contract.Kind == ExpandContractKind.DirectorSegments)
        {
            zhExtra += $"需要恰好 {expected} 组「提示词组」。每组按顺序包含：摘要 / 保留分析 / 详细描述 / 整体声景 / 非叙事配乐。目前已有 {groupsFound} 组。";
        }
        else if (contract.Kind == ExpandContractKind.FullReference)
        {
            zhExtra += "请补全六段：主体定义 / 摘要 / 保留分析 / 详细描述 / 整体声景 / 非叙事配乐。";
        }
        return zhExtra + "\n\n已写出（不要重写）：\n-----\n" + partial + "\n-----\n请紧接着最后一个字符继续。";
    }

    /// <summary>
    /// continueExpandUntilComplete 等价：finish_reason=length（或契约缺段）时续写，最多 3 次。
    /// 媒体契约（full_reference / director_segments）按 inspectExpandOutput 校验补全。
    /// </summary>
    internal static async Task<(string Text, bool Incomplete)> ContinueExpandUntilCompleteAsync(
        IReadOnlyList<ChatMessage> initialMessages,
        ExpandOutputContract contract,
        Func<IReadOnlyList<ChatMessage>, Task<ExpandChunk>> generate,
        Func<string, string> strip,
        CancellationToken ct,
        Action<int>? onContinue = null)
    {
        var messages = initialMessages.ToList();
        var assembled = "";
        var lastReason = "";
        var continues = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ExpandChunk result;
            try
            {
                result = await generate(messages);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (result.Error != null && string.IsNullOrWhiteSpace(result.Text))
            {
                if (assembled.Length > 0)
                {
                    var insp = InspectExpandOutput(assembled, contract);
                    return (assembled, ShouldContinueExpand(lastReason, insp));
                }
                throw new InferencesException(InferenceErrorKind.NetworkError, result.Error);
            }

            var chunk = strip(result.Text);
            lastReason = result.FinishReason;
            assembled = assembled.Length > 0 ? MergeContinuedText(assembled, chunk) : chunk;

            var inspect = InspectExpandOutput(assembled, contract);
            var more = ShouldContinueExpand(lastReason, inspect);
            if (!more || continues >= ExpandContinueMax)
            {
                return (assembled, more);
            }
            continues += 1;
            onContinue?.Invoke(continues);
            messages = initialMessages
                .Concat(new[]
                {
                    new ChatMessage { Role = "assistant", Content = assembled },
                    new ChatMessage { Role = "user", Content = BuildContinueUserMessage(assembled, contract, inspect) },
                })
                .ToList();
        }
    }

    private static bool ShouldContinueExpand(string finishReason, ExpandOutputInspect inspect)
        => inspect == null || !inspect.Complete || IsLengthFinish(finishReason);

    // ============================================================
    // 契约：resolveExpandOutputContract + inspectExpandOutput（expandOutputContinue.js 等价）
    // ============================================================

    internal sealed class ExpandOutputInspect
    {
        public bool Complete { get; set; }
        public string Kind { get; set; } = "none";
        public int GroupsFound { get; set; }
        public int ExpectedGroups { get; set; }
        public List<string> Missing { get; set; } = new();
    }

    private static readonly (string Id, string Zh, string En)[] GroupFields =
    {
        ("summary", "摘要", "summary"),
        ("retention", "保留分析", "retention_analysis"),
        ("detailed", "详细描述", "detailed_description"),
        ("soundscape", "整体声景", "overall_soundscape"),
        ("music", "非叙事配乐", "non_diegetic_music"),
    };

    private static readonly (string Id, string Zh, string En) SubjectField =
        ("subject", "主体定义", "subject_definitions");

    private static Regex LabelRegex(string zh, string en) =>
        new($"(^|\\n)\\s*(?:#{1,3}\\s*)?(?:\\*\\*)?(?:{Regex.Escape(zh)}|{Regex.Escape(en)})(?:\\*\\*)?\\s*[:：]",
            RegexOptions.IgnoreCase);

    private static bool HasLabel(string text, string zh, string en) =>
        LabelRegex(zh, en).IsMatch("\n" + (text ?? ""));

    private static int LabelBodyLen(string text, string zh, string en)
    {
        var src = "\n" + (text ?? "");
        var m = LabelRegex(zh, en).Match(src);
        if (!m.Success) return 0;
        var start = m.Index + m.Length;
        var rest = src[start..];
        var next = Regex.Match(rest, @"\n\s*(?:#{1,3}\s*)?(?:\*\*)?(?:[^\n]{1,40})(?:\*\*)?\s*[:：]");
        var body = (next.Success ? rest[..next.Index] : rest).Trim();
        return body.Length;
    }

    /// <summary>resolveExpandOutputContract：按 peId 场景 outputMode 解析契约。</summary>
    internal static ExpandOutputContract ResolveExpandContract(
        string? peId, string outputLang, IReadOnlyDictionary<string, string>? minimaxForm)
    {
        var scenario = MiniMaxScenarios.GetScenarioByPeId(peId);
        var lang = outputLang == "en" ? "en" : "zh";
        if (scenario == null) return new ExpandOutputContract { Kind = ExpandContractKind.None, Lang = lang };
        if (scenario.OutputMode == "director_segments")
        {
            var n = 4;
            if (minimaxForm != null && minimaxForm.TryGetValue("segment_count", out var sc)
                && int.TryParse(sc, out var sn) && sn > 0) n = sn;
            n = Math.Max(2, n);
            return new ExpandOutputContract { Kind = ExpandContractKind.DirectorSegments, Lang = lang, ExpectedGroups = n };
        }
        if (scenario.OutputMode == "full_reference")
        {
            return new ExpandOutputContract { Kind = ExpandContractKind.FullReference, Lang = lang };
        }
        return new ExpandOutputContract { Kind = ExpandContractKind.Loose, Lang = lang };
    }

    /// <summary>splitDirectorGroups：===== 提示词组 k ===== 分组。</summary>
    private static List<(int N, string Body)> SplitDirectorGroups(string text)
    {
        var src = text ?? "";
        var re = new Regex(@"={3,}\s*(?:提示词组|prompt\s*group)\s*(\d+)\s*={3,}", RegexOptions.IgnoreCase);
        var hits = new List<(int Index, int N, int End)>();
        foreach (Match m in re.Matches(src))
        {
            var n = int.TryParse(m.Groups[1].Value, out var gn) && gn > 0 ? gn : hits.Count + 1;
            hits.Add((m.Index, n, m.Index + m.Length));
        }
        if (hits.Count == 0) return new List<(int, string)>();
        var groups = new List<(int, string)>();
        for (var i = 0; i < hits.Count; i += 1)
        {
            var from = hits[i].End;
            var to = i + 1 < hits.Count ? hits[i + 1].Index : src.Length;
            groups.Add((hits[i].N, src[from..to]));
        }
        return groups;
    }

    /// <summary>inspectExpandOutput：full_reference 六段 / director_segments 公共设定+N 组校验。</summary>
    internal static ExpandOutputInspect InspectExpandOutput(string text, ExpandOutputContract contract)
    {
        var src = (text ?? "").Trim();
        var kind = (contract?.Kind ?? ExpandContractKind.None).ToString().ToLowerInvariant();
        var inspect = new ExpandOutputInspect
        {
            Kind = kind,
            ExpectedGroups = contract?.ExpectedGroups ?? 0,
        };
        if (src.Length == 0)
        {
            inspect.Missing.Add("empty");
            return inspect;
        }
        if (kind == "none" || kind == "loose")
        {
            inspect.Complete = true;
            return inspect;
        }

        if (kind == "fullreference")
        {
            var missing = new List<string>();
            foreach (var f in GroupFields.Prepend(SubjectField))
            {
                if (!HasLabel(src, f.Zh, f.En)) missing.Add(f.Id);
            }
            var lastBody = LabelBodyLen(src, GroupFields[^1].Zh, GroupFields[^1].En);
            if (lastBody < 2 && !missing.Contains("music")) missing.Add("music_body");
            inspect.Complete = missing.Count == 0;
            inspect.GroupsFound = missing.Count == 0 ? 1 : 0;
            inspect.ExpectedGroups = 1;
            inspect.Missing = missing;
            return inspect;
        }

        var expectedGroups = inspect.ExpectedGroups;
        var groups = SplitDirectorGroups(src);
        var miss = new List<string>();
        if (!HasLabel(src, SubjectField.Zh, SubjectField.En)) miss.Add("subject");
        if (groups.Count < expectedGroups) miss.Add($"groups:{groups.Count}/{expectedGroups}");
        foreach (var g in groups)
        {
            foreach (var f in GroupFields)
            {
                if (!HasLabel(g.Body, f.Zh, f.En)) miss.Add($"{g.N}:{f.Id}");
            }
            var gLast = LabelBodyLen(g.Body, GroupFields[^1].Zh, GroupFields[^1].En);
            if (gLast < 2) miss.Add($"{g.N}:music_body");
        }
        inspect.Complete = miss.Count == 0 && groups.Count >= expectedGroups;
        inspect.GroupsFound = groups.Count;
        inspect.Missing = miss;
        return inspect;
    }

    // ============================================================
    // 媒体多模态组装（prompt_master.js _buildMediaExpandMultimodalUser 等价，2070-2205）
    // 图片：data URL 缩边 1280 → image_url part；
    // 视频：zhipu ≤40MB 内联 video_url / 超限抽 6 帧；非 zhipu 抽 3 帧（无 ffmpeg 时 path-only）
    // 音频/文件：path-only 文本提示
    // ============================================================

    internal sealed class MediaExpandUserResult
    {
        public string Mode { get; init; } = "text"; // text | openai
        public object? Content { get; init; }
        public bool HasVideoMedia { get; init; }
    }

    /// <summary>zhipu 视频内联上限（对齐 ZHIPU_REVERSE_VIDEO_MAX_BYTES = 40MB）。</summary>
    private const long ZhipuReverseVideoMaxBytes = 40L * 1024 * 1024;

    /// <summary>provider 是否智谱（对齐 provider === 'zhipu'：按名称/BaseUrl 含 zhipu/智谱/bigmodel 判定）。</summary>
    internal static bool IsZhipuProvider(string? providerName, string? baseUrl)
        => providerName?.Contains("zhipu", StringComparison.OrdinalIgnoreCase) == true
           || providerName?.Contains("智谱", StringComparison.Ordinal) == true
           || baseUrl?.Contains("zhipu", StringComparison.OrdinalIgnoreCase) == true
           || baseUrl?.Contains("bigmodel", StringComparison.OrdinalIgnoreCase) == true;

    internal static string VideoMimeForPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4",
        };
    }

    internal static async Task<MediaExpandUserResult> BuildMediaExpandMultimodalUserAsync(
        string userText, IReadOnlyList<string> mediaPaths,
        string? providerName = null, string? baseUrl = null)
    {
        var items = ExpandMedia.EnumerateTaggedMedia(mediaPaths);
        var text = userText ?? "";
        if (items.Count == 0)
        {
            return new MediaExpandUserResult { Mode = "text", Content = text };
        }

        var parts = new List<ContentPart>
        {
            new()
            {
                Type = "text",
                Text = text +
                    "\n\n【以下多媒体与标签一一对应；请先视觉理解再写提示词】\n" +
                    "Each following media block is labeled with its identity tag. Do not swap identities.\n",
            },
        };
        var hasBinary = false;
        var hasVideo = false;
        var isZhipu = IsZhipuProvider(providerName, baseUrl);
        foreach (var item in items)
        {
            if (item.Kind == "image")
            {
                if (!File.Exists(item.Path)) continue;
                var url = MediaVision.PrepareVisionImageDataUrl(item.Path);
                if (url == null) continue;
                parts.Add(new ContentPart { Type = "text", Text = $"\nThis image IS {item.Tag} (file: {item.Base}):" });
                parts.Add(new ContentPart { Type = "image_url", ImageUrl = new ImageUrlPart { Url = url } });
                hasBinary = true;
            }
            else if (item.Kind == "video")
            {
                hasVideo = true;
                if (!File.Exists(item.Path)) continue;
                if (isZhipu)
                {
                    // 对齐 zhipu 分支：≤40MB 内联 video_url，超限抽 6 帧
                    var size = new FileInfo(item.Path).Length;
                    if (size <= ZhipuReverseVideoMaxBytes)
                    {
                        var b64 = Convert.ToBase64String(File.ReadAllBytes(item.Path));
                        parts.Add(new ContentPart { Type = "text", Text = $"\nThis video IS {item.Tag} (file: {item.Base}):" });
                        parts.Add(new ContentPart
                        {
                            Type = "video_url",
                            VideoUrl = new VideoUrlPart { Url = $"data:{VideoMimeForPath(item.Path)};base64,{b64}" },
                        });
                        hasBinary = true;
                    }
                    else
                    {
                        var frames = await TryExtractFramesAsync(item.Path, 6);
                        if (frames.Count > 0)
                        {
                            parts.Add(new ContentPart
                            {
                                Type = "text",
                                Text = $"\nVideo {item.Tag} ({item.Base}) exceeds API size; these keyframes represent it:",
                            });
                            foreach (var f in frames)
                            {
                                parts.Add(new ContentPart
                                {
                                    Type = "image_url",
                                    ImageUrl = new ImageUrlPart { Url = $"data:image/jpeg;base64,{f}" },
                                });
                            }
                            hasBinary = true;
                        }
                    }
                }
                else
                {
                    // 对齐通用分支：抽 3 帧（对齐 PromptMaster：帧空则静默跳过该视频，无帧贡献不置 hasBinary）
                    var frames = await TryExtractFramesAsync(item.Path, 3);
                    if (frames.Count > 0)
                    {
                        parts.Add(new ContentPart
                        {
                            Type = "text",
                            Text = $"\nThese keyframes are from {item.Tag} (file: {item.Base}). Treat them as one video:",
                        });
                        foreach (var f in frames)
                        {
                            parts.Add(new ContentPart
                            {
                                Type = "image_url",
                                ImageUrl = new ImageUrlPart { Url = $"data:image/jpeg;base64,{f}" },
                            });
                        }
                        hasBinary = true;
                    }
                }
            }
            else if (item.Kind == "audio")
            {
                parts.Add(new ContentPart
                {
                    Type = "text",
                    Text = $"\n{item.Tag} is audio reference {item.Base} (path only; no audio bytes attached).",
                });
            }
            else
            {
                parts.Add(new ContentPart
                {
                    Type = "text",
                    Text = $"\n{item.Tag} is file {item.Base} (path only).",
                });
            }
        }
        if (!hasBinary)
        {
            return new MediaExpandUserResult { Mode = "text", Content = text, HasVideoMedia = hasVideo };
        }
        return new MediaExpandUserResult { Mode = "openai", Content = parts, HasVideoMedia = hasVideo };
    }

    /// <summary>尝试抽帧；无 ffmpeg / 全部失败 → 空列表（fallback path-only，不中断主流程）。</summary>
    private static async Task<List<string>> TryExtractFramesAsync(string videoPath, int maxFrames)
    {
        try
        {
            var frames = await VideoFrameExtractor.ExtractFramesBase64Async(videoPath, maxFrames);
            if (frames.Count == 0)
                LogService.Instance.Warn(string.Format(Localizer.Instance?["ExpandVideoPathOnlyLogFormat"] ?? "", videoPath), "Expand");
            return frames;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn(string.Format(Localizer.Instance?["ExpandFrameExtractFailedLogFormat"] ?? "", ex.Message, videoPath), "Expand", ex);
            return new List<string>();
        }
    }

    // ============================================================
    // 清洗：stripZhipuModelArtifacts + _stripThinkingTags（zhipuChatText.js + pm_text_expand.js:78）
    // ============================================================

    /// <summary>GLM 特殊 token（GLM_SPECIAL_TOKEN_RE）。</summary>
    internal static readonly Regex GlmSpecialTokenRe = new(
        "<\\|(?:observation|assistant|user|system|tool_call|tool_output|begin_of_box|end_of_box|im_start|im_end)\\|>",
        RegexOptions.IgnoreCase);

    /// <summary>去掉 /&lt;think&gt; 块、思考：行、GLM 特殊 token、&lt;answer&gt; 标签。</summary>
    internal static string StripThinkingTags(string text)
    {
        var s = text ?? "";
        s = Regex.Replace(s, "[\\s\\S]*?</think>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "^\\s*思考[：:][\\s\\S]*?(?=\n\n|\n[^\\s]|$)", "", RegexOptions.Multiline);
        s = GlmSpecialTokenRe.Replace(s, "");
        s = Regex.Replace(s, "</?answer>", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    // ============================================================
    // 质量词前缀（prompt_master.js _applyExpandQualityPrefix）
    // ============================================================

    internal static string ApplyExpandQualityPrefix(string text, ExpandRequest req)
    {
        var enabled = req.QualityPromptEnabled;
        if (!enabled) return (text ?? "").Trim();
        var type = req.Type == "Danbooru_tag_list" ? "Danbooru_tag_list" : "Stable_Diffusion_Prompt";
        var cap = new ReverseCaptionRequest
        {
            Type = type,
            QualityPromptEnabled = true,
            QualityPromptPrefix = req.QualityPromptPrefix ?? "",
        };
        return CaptionPromptBlocks.ApplyQualityPrefixToCaption(text ?? "", cap);
    }

    // ============================================================
    // 模型 max_tokens 上限（prompt_master.js _zhipuClampMaxTokens 等价）
    // ============================================================

    private static int ClampMaxTokens(string model, int maxTokens)
    {
        var m = (model ?? "").ToLowerInvariant();
        int cap;
        if (Regex.IsMatch(m, "4\\.5v|4\\.6v|4\\.7v|4\\.1v-thinking|4v-plus"))
        {
            cap = 4096;
        }
        else if (Regex.IsMatch(m, "4v|4\\.5v|4\\.6v|5v|6v|vision|(?:^|[-_.])vl(?:[-_.]|$)"))
        {
            cap = 1024;
        }
        else
        {
            cap = 4096;
        }
        return Math.Min(maxTokens, cap);
    }
}
