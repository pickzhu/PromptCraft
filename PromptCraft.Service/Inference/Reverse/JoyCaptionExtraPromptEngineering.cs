using PromptCraft.Models.Inference;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// JoyCaption「更多设置」在提示词工程中的强化遵循块
// 1:1 移植自 app/electron/config/joyCaptionExtraPromptEngineering.js
// ============================================================

public static class JoyCaptionExtraPromptEngineering
{
    private const string JoyNoUnchangeableTraitsOptionId = "no_unchangeable_traits";

    private static bool IsZhCaption(ReverseCaptionRequest caption)
        => (caption.CaptionLang ?? "en") == "zh";

    public static bool HasActiveJoyExtraOptions(ReverseCaptionRequest caption)
    {
        var ids = JoyCaptionExtraOptions.ResolveJoyExtraOptionIds(caption);
        if (ids.Count > 0)
            return true;
        var ep = (caption.ExtraPrompt ?? "").Trim();
        return ep.Length > 0;
    }

    private static string BuildConflictOverrides(ReverseCaptionRequest caption, bool zh)
    {
        var parts = new List<string>();

        if (JoyCaptionExtraOptions.IsSceneOnlyNoCharacterAppearance(caption))
        {
            parts.Add(zh
                ? "不写外貌/衣着：还原检查表、五点结构中与面部细节、发型、肤色、体型、服装材质相关的维度一律跳过，最终输出不得出现任何外貌特征或衣物描述。"
                : "No appearance/clothing: skip checklist and framework items about face, hair, skin, body shape, or garments; output must contain zero appearance or clothing tokens.");
        }

        if (JoyCaptionExtraOptions.HasNoGlassesHeadwearOption(caption))
        {
            parts.Add(zh
                ? "不写眼镜/头饰：即使面部检查表要求写眼镜、面具或头饰，也必须完全省略。"
                : "No glasses/headwear: omit all eyewear and headwear even if a face checklist would require them.");
        }

        if (JoyCaptionExtraOptions.HasNoArtisticStyleOption(caption))
        {
            parts.Add(zh
                ? "不写风格：媒介、画风、渲染方式、质量词、美学用语及类似风格标签一律跳过，不得写入最终输出。"
                : "No style: skip medium, art style, rendering, quality tags, and aesthetic labels; none may appear in the final output.");
        }

        if (JoyCaptionExtraOptions.HasJoyExtraOption(caption, JoyNoUnchangeableTraitsOptionId))
        {
            parts.Add(zh
                ? "不写不可变属性：勿写种族、性别、年龄等不可变身份信息；若与「不写外貌」冲突，以不写外貌为准。"
                : "No unchangeable traits: omit ethnicity, gender, age, etc.; if this conflicts with no-appearance, no-appearance wins.");
        }

        var name = JoyCaptionExtraOptions.ResolveJoyCharacterName(caption);
        if (JoyCaptionExtraOptions.HasJoyCharacterNameOption(caption) && name.Length > 0)
        {
            parts.Add(zh
                ? $"统一称呼：人物/角色必须且仅能使用「{name}」指称，禁止使用 person、character、人物、角色 等泛称替代。"
                : $"Character name: refer to the person/character ONLY as \"{name}\"; never use generic labels like person, character, figure, or subject.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>system：更多设置为最高优先级，覆盖与检查表冲突的默认 PE 指令。</summary>
    public static string BuildJoyExtraSystemEnforcementBlock(ReverseCaptionRequest caption, string mediaTarget)
    {
        _ = mediaTarget;
        if (!HasActiveJoyExtraOptions(caption))
            return "";

        var zh = IsZhCaption(caption);
        var overrides = BuildConflictOverrides(caption, zh);

        var preamble = zh
            ? "【更多设置·最高优先级】任务末尾「附加要求」中用户勾选的打标选项为硬性约束，优先于本提示中的还原检查表、极致还原框架、「有则必写」清单及任何与之冲突的工程默认指令。必须逐条严格执行；所有 Do NOT / 不要 / 禁止 类条款若在输出中出现对应描述即视为失败。"
            : "[MORE SETTINGS · HIGHEST PRIORITY] User-selected caption options in Additional requirements at the end of the task are HARD constraints. They override faithfulness checklists, reproduction frameworks, \"must include\" lists, and any conflicting default PE instructions. Execute every line strictly; forbidden topics must be absent from the final output.";

        var verify = zh
            ? "生成最终标注前请在内心核对附加要求是否全部满足，但不要在输出中写自检、分析或 Markdown。"
            : "Mentally verify all additional requirements before answering; do not output self-checks, analysis, or markdown.";

        if (overrides.Length == 0)
            return $"\n\n{preamble} {verify}";

        var label = zh ? "针对已启用选项：" : "Enabled overrides: ";
        return $"\n\n{preamble} {label}{overrides} {verify}";
    }

    /// <summary>user：紧挨「附加要求」之前，再次强调优先级。</summary>
    public static string BuildJoyExtraUserEnforcementTail(ReverseCaptionRequest caption)
    {
        if (!HasActiveJoyExtraOptions(caption))
            return "";

        var zh = IsZhCaption(caption);
        return zh
            ? " 【附加要求优先级】以下「附加要求」为最高优先级硬性约束；若与上文任务句、还原检查表或输出格式说明冲突，必须以附加要求为准，禁止输出附加要求所禁止的内容。"
            : " [Additional requirements priority] The following Additional requirements are HARD constraints; if they conflict with earlier task lines, checklists, or format rules, Additional requirements win—never output forbidden content.";
    }
}
