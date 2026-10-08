using PromptCraft.Models.Inference;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// ToriiGate-0.5 官方打标格式元数据
// 1:1 移植自 app/electron/config/toriiGateFormats.js
// ============================================================

public static class ToriiGateFormats
{
    /// <summary>ToriiGate 10 种结构化格式（TORII_GATE_FORMATS）。</summary>
    public static readonly IReadOnlyList<ToriiGateFormat> All = new List<ToriiGateFormat>
    {
        new()
        {
            CType = "long_thoughts_v2",
            CTypeRuntime = "long_thoughts_v2",
            Name = "结构化 MD · 四段详述",
            Description = "4 段 Markdown：① 角色思考 ② Key details ③ Long description ④ 分角色详述（## 角色名）。建议配合角色名列表；不含部件列表/图中文字/背景专段。",
            Category = "详细结构化",
            UseNamesDefault = true,
            ExtractMode = "full",
            Sort = 110,
        },
        new()
        {
            CType = "long_thoughts",
            CTypeRuntime = "long_thoughts",
            Name = "结构化 MD · 六段完整式",
            Description = "6 段 Markdown：① 思考 ② General description ③ 分角色 ④ Individual Parts ⑤ Texts on image ⑥ Background and effects。比 v2 多部件/文字/背景段，适合复杂图与数据集。",
            Category = "详细结构化",
            UseNamesDefault = true,
            ExtractMode = "full",
            Sort = 120,
        },
        new()
        {
            CType = "min_structured_md",
            CTypeRuntime = "min_structured_md",
            Name = "结构化 MD · 极简三段",
            Description = "短结构化 Markdown；含前两段推理，成品提示词请用「仅结构化正文」变体或自行去掉 §1–2。",
            Category = "即用提示词",
            UseNamesDefault = true,
            ExtractMode = "full",
            Sort = 140,
        },
        new()
        {
            CType = "min_structured_md_body",
            CTypeRuntime = "min_structured_md",
            Name = "结构化 MD · 仅正文三段",
            Description = "同 min_structured_md，自动去掉 §1 Thoughts 与 §2 Key details，保留 §3 Structured description。",
            Category = "即用提示词",
            UseNamesDefault = true,
            ExtractMode = "min_md_body",
            Sort = 145,
        },
        new()
        {
            CType = "min_structured_json",
            CTypeRuntime = "min_structured_json",
            Name = "结构化 JSON · 极简键值",
            Description = "按角色/General 等键的 JSON；输出为标准 JSON 文本（非 key: 展平）。",
            Category = "即用提示词",
            UseNamesDefault = false,
            ExtractMode = "json_raw",
            Sort = 150,
        },
        new()
        {
            CType = "json",
            CTypeRuntime = "json",
            Name = "结构化 JSON · 标准字段",
            Description = "character/background/atmosphere 等字段；输出为标准 JSON 文本。",
            Category = "即用提示词",
            UseNamesDefault = false,
            ExtractMode = "json_raw",
            Sort = 160,
        },
        new()
        {
            CType = "long",
            CTypeRuntime = "long",
            Name = "自然语言 · 多段长描述",
            Description = "2–5 段自然语言长描述，无 Markdown 结构，支持角色名。",
            Category = "Legacy",
            UseNamesDefault = false,
            ExtractMode = "full",
            Sort = 170,
        },
        new()
        {
            CType = "short",
            CTypeRuntime = "short",
            Name = "自然语言 · 短描述",
            Description = "简短扼要，覆盖主要对象与细节，无冗长修辞。",
            Category = "Legacy",
            UseNamesDefault = false,
            ExtractMode = "full",
            Sort = 180,
        },
        new()
        {
            CType = "md_comic",
            CTypeRuntime = "md_comic",
            Name = "结构化 MD · 漫画分镜",
            Description = "漫画/分镜专用 Markdown：格式说明、逐格描述与总结，含推理段。",
            Category = "漫画",
            UseNamesDefault = true,
            ExtractMode = "full",
            Sort = 190,
        },
        new()
        {
            CType = "json_comic",
            CTypeRuntime = "json_comic",
            Name = "结构化 JSON · 漫画分帧",
            Description = "按帧与角色的 JSON 漫画描述；输出为标准 JSON 文本。",
            Category = "漫画",
            UseNamesDefault = false,
            ExtractMode = "json_raw",
            Sort = 200,
        },
    };

    /// <summary>构建 torii 内置 PE profile 列表（buildToriiReverseProfiles）。</summary>
    public static List<PromptEngineeringProfile> BuildToriiReverseProfiles()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<PromptEngineeringProfile>();
        foreach (var f in All)
        {
            list.Add(new PromptEngineeringProfile
            {
                Id = $"pe_torii_{System.Text.RegularExpressions.Regex.Replace(f.CType, "[^a-z0-9]+", "_")}",
                Kind = "reverse",
                Builtin = true,
                StructuredFormat = true,
                ToriiFormat = true,
                BuiltinKey = f.CTypeRuntime,
                ToriiExtractMode = f.ExtractMode,
                ToriiUseNamesDefault = f.UseNamesDefault,
                Name = f.Name,
                Category = $"结构化 / {f.Category}",
                Description = f.Description,
                Enabled = true,
                Sort = f.Sort,
                OutputFormat = OutputFormatByCType(f.CType),
                Tags = new List<string> { "结构化", f.Category },
                SubjectDomains = new List<string> { "general", "portrait", "character", "comic", "complex" },
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        return list;
    }

    private static string OutputFormatByCType(string cType)
        => cType switch
        {
            "json" or "min_structured_json" or "json_comic" => "structured_json",
            // 对齐 PM promptEngineeringTaxonomy.TORII_OUTPUT_FORMAT_BY_CTYPE：
            // long / short 属于「自然语言」（prose），不是结构化 MD
            "long" or "short" => "prose",
            _ => "structured_md",
        };

    /// <summary>是否为结构化反推 PE id（isStructuredReverseProfileId）。</summary>
    public static bool IsStructuredReverseProfileId(string? profileId)
    {
        var id = profileId ?? "";
        return id.StartsWith("pe_torii_", StringComparison.Ordinal)
            || id.StartsWith("pe_expand_torii_", StringComparison.Ordinal);
    }

    /// <summary>是否为结构化模板 PE（isStructuredTemplateProfile）。</summary>
    public static bool IsStructuredTemplateProfile(PromptEngineeringProfile? profile)
        => profile != null
           && (profile.StructuredFormat || profile.ToriiFormat || IsStructuredReverseProfileId(profile.Id));

    /// <summary>反推 caption 是否应走结构化模板提示词（usesStructuredPromptCaption，仅由 PE 决定，与模型无关）。</summary>
    public static bool UsesStructuredPromptCaption(ReverseCaptionRequest caption)
    {
        if (caption == null)
            return false;
        var peFmt = (caption.PeOutputFormat() ?? "").Trim();
        if (peFmt == "structured_md" || peFmt == "structured_json")
            return true;
        var type = (caption.Type ?? "").Trim();
        return type == "Structured" || type == "ToriiGate";
    }
}

/// <summary>ReverseCaptionRequest 的 PE 输出格式访问器扩展。</summary>
public static class ReverseCaptionPeExtensions
{
    public static string? PeOutputFormat(this ReverseCaptionRequest caption)
    {
        // pe_output_format 由 applyReverseToCaption 写入 caption 内部状态；
        // 这里通过附加字段承载（见 PmPromptEngineeringService 的应用结果）。
        return caption.PeOutputFormatValue;
    }

    public static string? StructuredFormat(this ReverseCaptionRequest caption)
        => caption.StructuredFormatValue;

    public static string? ToriiFormat(this ReverseCaptionRequest caption)
        => caption.ToriiFormatValue;

    public static string ToriiExtractMode(this ReverseCaptionRequest caption)
        => string.IsNullOrEmpty(caption.ToriiExtractModeValue) ? "full" : caption.ToriiExtractModeValue;
}
