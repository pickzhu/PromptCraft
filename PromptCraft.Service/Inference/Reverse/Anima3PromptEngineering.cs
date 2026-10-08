using PromptCraft.Models.Inference;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// ANIMA3 提示词生成模板 v3.0
// 1:1 移植自 app/electron/config/anima3PromptEngineering.js
// caption.anima3_enhance === true 时：本模块规则为最高优先级，覆盖 ComfyUI 还原检查表 / 中文逗号 SD 等其它约定。
// ============================================================

public static class Anima3PromptEngineering
{
    private static bool IsZh(ReverseCaptionRequest caption)
        => (caption.CaptionLang ?? "en") == "zh";

    private static (string Band, string Tier) ResolveTagCountBand(ReverseCaptionRequest caption)
    {
        var (band, tier) = CaptionLength.ResolveAnima3TagCountBand(caption.Len, caption.CaptionLenChars);
        return (band, tier);
    }

    private const string PriorityZh =
        "【最高优先级·ANIMA3 v3.0】以下规则优先于 ComfyUI 还原检查表、中文逗号标签、叙事描写等一切其它打标约定；冲突时严格执行 ANIMA3。";
    private const string PriorityEn =
        "[TOP PRIORITY · ANIMA3 v3.0] These rules OVERRIDE ComfyUI fidelity checklists, Chinese comma tags, and any other caption conventions.";

    private const string NoMetaZh =
        "【严禁输出】自检/分析/小标题/Markdown（含 ** 标题）、self-check、fidelity analysis；内心完成检查，回复有且仅能是一行 tag。";
    private const string NoMetaEn =
        "[FORBIDDEN OUTPUT] No self-check, fidelity analysis, headings, markdown—internal checks only; reply MUST be ONE tag line.";

    private static string BuildRoleBlock(bool zh)
    {
        if (zh)
        {
            return " 【§1 ROLE】你是 Anima3 提示词工程师。唯一职责：把所见画面转写为一条英文 prompt（仅 content 部分）。" +
                " 必须：按 §4 槽位顺序填 tag；按 §2 格式输出；内心完成 §3 自检与 §3.1 互斥检查。" +
                " 禁止：解释、寒暄、Markdown、质量词、画师名、权重语法、光线/光影/色调类 tag（脚本与 LoRA 已处理）。";
        }
        return " [§1 ROLE] You are an Anima3 prompt engineer. Sole job: ONE English content prompt from the image." +
            " MUST: §4 slot order; §2 output protocol; §3/§3.1 internal self-check & conflicts." +
            " FORBIDDEN: prose, markdown, quality tags, @artist, weights, lighting/color-grade tags (handled elsewhere).";
    }

    private static string BuildOutputProtocolBlock(bool zh)
    {
        if (zh)
        {
            return " 【§2 OUTPUT PROTOCOL】仅 1 行、无换行；标签间 \", \"（逗号+空格）；全部 lowercase（score_ 保留下划线）；" +
                " 禁止用方括号包裹 tag（错误示例：[girl]、[black hood]；正确：1girl, black_hood）；" +
                " 禁止 masterpiece/best quality/score_*、@artist、(tag:1.2) 权重；" +
                " 禁止 sunlight/moonlight/rim light/warm lighting/god rays/backlighting 等光线光影色调（允许 rain/snow/fog/steam/night 等天气）；" +
                " 纯文本一行，无 code fence；tag 说不清时用英文短句补充，且必须放在所有 tag 之后。";
        }
        return " [§2 OUTPUT PROTOCOL] Exactly 1 line; \", \" separators; all lowercase; NO square brackets around tags (use 1girl, black_hood—not [girl]); no quality/artist/weights;" +
            " no lighting/color-grade tags (weather like rain/snow/fog OK); NL supplement only AFTER all tags.";
    }

    private static string BuildSelfCheckBlock(bool zh)
    {
        return zh
            ? " 【§3 内心自检·勿写出】①人数与 count 一致 ②无 §3.1 互斥 ③无重复 tag ④场景与动作物理合理 ⑤无光线禁令 tag ⑥总量在 §4.2 范围。"
            : " [§3 INTERNAL SELF-CHECK] count match; no §3.1 conflicts; no duplicates; scene-action OK; no banned lighting; tag count in §4.2 range.";
    }

    private static string BuildConflictBlock(bool zh)
    {
        return zh
            ? " 【§3.1 互斥速查】视角：from front↔from behind、looking at viewer↔facing away、pov↔full body、close-up↔full body；" +
              " 身份：solo↔hetero/1boy、sleeping↔looking at viewer、blindfold↔rolling eyes；" +
              " 服装：completely nude↔具体服装、pantyhose↔barefoot（torn pantyhose 除外）、blindfold↔glasses；" +
              " 动作：missionary↔doggystyle、spread legs↔legs together；同部位细节 tag ≤2 且不矛盾（禁 spread toes+toe scrunch）。"
            : " [§3.1 CONFLICTS] No front+behind; solo+hetero; nude+clothing; pantyhose+barefoot; missionary+doggystyle; spread legs+legs together; ≤2 detail tags/body part, no contradictions.";
    }

    private static string BuildSlotOrderBlock(bool zh, string band, string tier)
    {
        const string order = "count/gender → character/series → appearance → clothing/state → pose/action/sex → expression/reaction → camera/shot → scene/environment → detail/mood → NL tail";
        if (zh)
        {
            return $" 【§4 SLOT ORDER】严格顺序：{order}。靠前槽位权重更高。" +
                $" 【§4.2 总量】约 {band} 个 tag（{tier}：简单16-30/标准22-38/复杂30-48）；服装槽可略多，其余精简，禁止重复 tag。" +
                " 【§4.1 风格一致】服装/场景/detail 同一世界观（禁 hanfu+cyberpunk 混搭）。" +
                " 【§4.3 视线】单人默认 direct eye contact, facing viewer（除非背影/侧脸）；多人用 looking at another，勿强行全员看镜头。" +
                " 【§4.4 自然语言】仅 tag 无法表达多人归属/复杂构图/特殊姿势/分镜时，英文短句放 prompt 末尾。" +
                " 【§4.6 多人】每角色须有发色+瞳色+关键特征短语，再写共享 pose/scene；关系放末尾 NL。";
        }
        return $" [§4 SLOT ORDER] {order}; front slots weigh more." +
            $" [§4.2 COUNT] ~{band} tags ({tier})." +
            " [§4.1] Consistent worldview across clothing/scene/mood." +
            " [§4.3] SOLO: direct eye contact, facing viewer unless back/profile." +
            " [§4.4] NL phrase at END only when tags insufficient." +
            " [§4.6] Multi-subject: per-character appearance before shared tags.";
    }

    private static string BuildAssemblyTreeBlock(bool zh)
    {
        return zh
            ? " 【§5 决策树·先匹配再填槽】5.1单人展示 5.2双人前戏 5.3双人正戏 5.4特殊体位(睡奸/催眠/femdom/过激) 5.5多人 5.6百合 5.7特殊主题(先查§14配方再填)；" +
              " 按匹配类型侧重各槽位（appearance/clothing/pose/expression/camera/scene/detail），勿堆无关 tag。"
            : " [§5 ASSEMBLY] Match 5.1 solo / 5.2 foreplay / 5.3 sex / 5.4 special pose / 5.5 group / 5.6 yuri / 5.7 special theme—fill slots accordingly.";
    }

    private static string BuildDanbooruPrefixBlock(bool zh)
    {
        return zh
            ? " 【Danbooru·置于 count 之前】artist:, copyright:, character:, meta:（不确定 artist:unknown、copyright:original；无角色 character:none）；" +
              " IP 角色须 ≥5 外观锚点；禁止编造未知角色特征。"
            : " [Danbooru PREFIX before count] artist:/copyright:/character:/meta:; unknown/original/none; ≥5 appearance anchors for IP; no invented traits.";
    }

    /// <summary>勾选 ANIMA 时的唯一主系统提示（不叠加 ComfyUI 长检查表）。</summary>
    public static string BuildPrimarySystemPrompt(ReverseCaptionRequest caption, string mediaTarget, bool danbooru = false)
    {
        var zh = IsZh(caption);
        var video = (mediaTarget ?? caption.MediaTarget ?? "image") == "video";
        var (band, tier) = ResolveTagCountBand(caption);
        var media = video ? (zh ? "视频关键帧" : "video") : zh ? "图像" : "image";

        var out_ = zh ? $" {PriorityZh}" : $" {PriorityEn}";
        out_ += zh ? $" {NoMetaZh}" : $" {NoMetaEn}";
        out_ += BuildRoleBlock(zh);
        out_ += BuildOutputProtocolBlock(zh);
        out_ += BuildSelfCheckBlock(zh);
        out_ += BuildConflictBlock(zh);
        out_ += BuildSlotOrderBlock(zh, band, tier);
        out_ += BuildAssemblyTreeBlock(zh);
        if (danbooru)
            out_ += BuildDanbooruPrefixBlock(zh);
        if (video)
        {
            out_ += zh
                ? " 【视频】pose/action 与 camera/shot 须写动作、运镜、运动模糊（motion lines 与 motion blur 二选一）。"
                : " [VIDEO] Include motion, camera move, one of motion lines/motion blur.";
        }
        out_ += zh
            ? $" 观察{media}可见事实填槽，输出一条英文 prompt（约 {band} tags）。"
            : $" Observe this {media}; output one English prompt (~{band} tags).";
        return out_;
    }

    public static string BuildSystemAddons(ReverseCaptionRequest caption, string mediaTarget)
    {
        _ = mediaTarget;
        var zh = IsZh(caption);
        return zh
            ? " 【执行】仅按 ANIMA3 组装，勿改用 ComfyUI 中文关键词或分段分析。"
            : " [EXECUTE] ANIMA3 only—do not revert to ComfyUI checklist prose or Chinese comma tags.";
    }

    public static string BuildUserTaskLead(ReverseCaptionRequest caption, string mediaTarget)
    {
        var zh = IsZh(caption);
        var video = (mediaTarget ?? caption.MediaTarget ?? "image") == "video";
        var (band, _) = ResolveTagCountBand(caption);
        if (zh)
        {
            return video
                ? $"【ANIMA3 任务】按 §5 决策树与 §4 槽位顺序分析视频画面，直接输出约 {band} 个英文 tag 的一行 prompt，勿写分析过程。 "
                : $"【ANIMA3 任务】按 §5 决策树与 §4 槽位顺序分析图像，直接输出约 {band} 个英文 tag 的一行 prompt，勿写分析过程。 ";
        }
        return video
            ? $"[ANIMA3 TASK] Match §5 scene type, fill §4 slots from video, output one English line (~{band} tags). "
            : $"[ANIMA3 TASK] Match §5 scene type, fill §4 slots from image, output one English line (~{band} tags). ";
    }

    public static string BuildOutputConstraints(ReverseCaptionRequest caption)
    {
        var (band, _) = ResolveTagCountBand(caption);
        var zh = IsZh(caption);
        if (zh)
        {
            return $" 【ANIMA3 最终契约】有且仅一行英文 lowercase tag（约 {band} 个），\", \" 分隔，严格 §4 槽位顺序；{NoMetaZh}";
        }
        return $" [ANIMA3 FINAL] One lowercase English line (~{band} tags), \", \" separated, §4 slot order. {NoMetaEn}";
    }

    public static string BuildUserTaskBody(ReverseCaptionRequest caption, bool danbooru = false)
    {
        var zh = IsZh(caption);
        var (band, _) = ResolveTagCountBand(caption);
        if (zh)
        {
            var bodyZh = $"工作流：§5 匹配场景→§4 按槽位从画面提取→§3/§3.1 内心自检→输出一行英文 tag（约 {band} 个）。";
            if (danbooru)
                bodyZh += " 先 Danbooru 前缀再 count/gender。";
            return bodyZh;
        }
        var bodyEn = $"Workflow: §5 pick scene→§4 fill slots→§3 internal check→one English line (~{band} tags).";
        if (danbooru)
            bodyEn += " Danbooru prefixes first.";
        return bodyEn;
    }

    public static string BuildUserTailAddon()
        => " 现在只输出最终一行 tag，不要任何其它文字。";

    private static int CountSeparators(string s)
        => Regex.Matches(s ?? "", "[,，]").Count;

    private static bool IsMetaCaptionLine(string line)
    {
        var l = (line ?? "").Trim();
        if (l.Length == 0)
            return true;
        if (Regex.IsMatch(l, @"^#{1,6}\s"))
            return true;
        if (Regex.IsMatch(l, @"^\*\*[^*]+\*\*$", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(l,
                @"self[- ]?check|fidelity\s*analysis|checklist|quality\s*assurance|还原检查|自检|分析过程|thought\s*process|§\d",
                RegexOptions.IgnoreCase))
        {
            return true;
        }
        var seps = CountSeparators(l);
        if (seps == 0 && !Regex.IsMatch(l, @"^(artist|copyright|character|meta|score_|1girl|1boy|2girl|\d+girls?)", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    private static bool IsMetaTagFragment(string frag)
    {
        var t = (frag ?? "").Trim();
        if (t.Length == 0)
            return true;
        if (Regex.IsMatch(t, @"^\*\*[^*]+\*\*$", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(t, @"^(?:self[- ]?check|fidelity\s*analysis|checklist|还原检查|自检|§\d)", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    private static string NormalizeTagFragment(string frag)
    {
        var t = (frag ?? "").Trim().Replace("^\\*\\*", "").Replace("\\*\\*$", "");
        if (t.Length == 0 || IsMetaTagFragment(t))
            return "";
        if (Regex.IsMatch(t, @"^(artist|copyright|character|meta|score_):", RegexOptions.IgnoreCase))
        {
            var idx = t.IndexOf(':');
            return t.Substring(0, idx + 1).ToLowerInvariant() + t.Substring(idx + 1).Trim().ToLowerInvariant();
        }
        return t.ToLowerInvariant();
    }

    public static string SanitizeTagLine(string text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0)
            return s;

        var lines = s
            .Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None)
            .Select(l => l.Replace("^\\*\\*", "").Replace("\\*\\*$", "").Trim())
            .Where(l => l.Length > 0 && !IsMetaCaptionLine(l))
            .ToList();

        var best = lines.Count > 0 ? lines[0] : "";
        var bestSeps = CountSeparators(best);
        for (var i = 1; i < lines.Count; i++)
        {
            var c = CountSeparators(lines[i]);
            if (c > bestSeps)
            {
                bestSeps = c;
                best = lines[i];
            }
        }

        if (best.Length == 0 || IsMetaCaptionLine(best))
        {
            var inline = s.Replace("\r\n", " ").Replace("\n", " ").Replace("**", "").Trim();
            if (CountSeparators(inline) >= 1)
            {
                best = inline;
            }
            else if (!IsMetaCaptionLine(inline) && inline.Length > 0)
            {
                best = inline;
            }
            else
            {
                return "";
            }
        }

        best = Regex.Replace(best, @"^[`'""]+|[`'""]+$", "").Trim();
        var cleaned = TagLineSanitize.CleanTagLineBody(best);
        if (string.IsNullOrEmpty(cleaned))
            return "";

        var parts = Regex.Split(cleaned, "[,，]\\s*")
            .Select(NormalizeTagFragment)
            .Where(p => p.Length > 0)
            .ToList();
        if (parts.Count > 0)
            return string.Join(", ", parts);
        return NormalizeTagFragment(cleaned);
    }
}
