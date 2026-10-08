using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference.Minimax;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 提示词工程（Prompt Engineering）服务
// 1:1 移植自 app/electron/service/pm_prompt_engineering.js + config/promptEngineeringRegistry.js
//  + config/promptEngineeringResolver.js + config/promptEngineeringTaxonomy.js
// 核心：applyReverseToCaption（把所选 PE 映射到 caption）+ 内置 PE 注册表 + 自定义 PE 持久化 CRUD
// ============================================================

public static class PromptEngineeringRegistry
{
    /// <summary>内置反推 PE 3 个（BUILTIN_REVERSE_PROFILES）。</summary>
    public static readonly List<PromptEngineeringProfile> BuiltinReverseProfiles = new()
    {
        new()
        {
            Id = "pe_reverse_descriptive",
            Kind = "reverse",
            Builtin = true,
            BuiltinKey = "Descriptive",
            Name = "自然语言 · 五点结构式",
            Category = "reverse",
            Description = "单段连贯自然语言，按五点结构（构图、主体、环境、文字、风格）极致还原画面，适用于 Flux、MJ 等自然语言提示词模型。",
            Enabled = true,
            Sort = 10,
            OutputFormat = "prose",
            Tags = new List<string> { "五点结构", "极致还原", "自然语言" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_reverse_sd",
            Kind = "reverse",
            Builtin = true,
            BuiltinKey = "Stable_Diffusion_Prompt",
            Name = "SD 标签 · Stable Diffusion 提示词格式",
            Category = "reverse",
            Description = "一行逗号分隔 SD 正向标签，含完整还原检查表，适用于 SD1.5、SDXL、ComfyUI 等标签式模型。",
            Enabled = true,
            Sort = 20,
            OutputFormat = "sd_tags",
            Tags = new List<string> { "ComfyUI", "SD", "还原检查表" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_reverse_danbooru",
            Kind = "reverse",
            Builtin = true,
            BuiltinKey = "Danbooru_tag_list",
            Name = "Danbooru 标签 · 标准前缀式",
            Category = "reverse",
            Description = "一行英文 Danbooru 风格 tag（短语内空格，如 long hair），严格 artist:/copyright:/character: 前缀顺序，适用于二次元 LoRA 与 SD 训练。",
            Enabled = true,
            Sort = 30,
            OutputFormat = "danbooru_tags",
            Tags = new List<string> { "Danbooru", "标准前缀" },
            SubjectDomains = new List<string> { "general", "portrait" },
        },
    };

    /// <summary>内置训练打标 PE 12 条（trainCaptionTypeProfiles.js TRAIN_CAPTION_TYPE_DEFS 1:1 移植）。</summary>
    public static readonly List<PromptEngineeringProfile> BuiltinTrainProfiles = new()
    {
        new()
        {
            Id = "pe_train_descriptive", Kind = "train", Builtin = true, BuiltinKey = "Descriptive",
            Name = "描述式打标", Category = "train",
            Description = "单段连贯自然语言描述画面主体、环境与风格，适用于 Flux、MJ 等自然语言提示词模型训练。",
            Enabled = true, Sort = 10, OutputFormat = "prose",
            Tags = new List<string> { "描述式", "自然语言", "训练" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_train_descriptive_casual", Kind = "train", Builtin = true, BuiltinKey = "Descriptive_Casual",
            Name = "口语化描述式打标", Category = "train",
            Description = "轻松口语化的自然语言描述，语气更随意，仍覆盖画面关键信息。",
            Enabled = true, Sort = 15, OutputFormat = "prose",
            Tags = new List<string> { "口语化", "自然语言", "训练" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_train_straightforward", Kind = "train", Builtin = true, BuiltinKey = "Straightforward",
            Name = "简洁直述式描述", Category = "train",
            Description = "客观直述画面要素（主体、颜色、形状、空间关系等），避免主观臆测与「这是一张…」式开头。",
            Enabled = true, Sort = 20, OutputFormat = "prose",
            Tags = new List<string> { "直述", "自然语言", "训练" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_train_sd", Kind = "train", Builtin = true, BuiltinKey = "Stable_Diffusion_Prompt",
            Name = "Stable Diffusion 提示词格式", Category = "train",
            Description = "一行逗号分隔 SD 正向标签，含完整还原检查表，适用于 SD1.5、SDXL、ComfyUI 等标签式模型训练。",
            Enabled = true, Sort = 25, OutputFormat = "sd_tags",
            Tags = new List<string> { "SD", "ComfyUI", "训练" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_train_midjourney", Kind = "train", Builtin = true, BuiltinKey = "MidJourney",
            Name = "MidJourney 提示词格式", Category = "train",
            Description = "一行或极短一段 MidJourney 风格英文关键词与参数式短语。",
            Enabled = true, Sort = 30, OutputFormat = "sd_tags",
            Tags = new List<string> { "MidJourney", "关键词", "训练" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_train_danbooru", Kind = "train", Builtin = true, BuiltinKey = "Danbooru_tag_list",
            Name = "Danbooru 标签列表（动漫）", Category = "train",
            Description = "一行英文 Danbooru 风格 tag（artist:/copyright:/character:/meta: 前缀 + 通用标签），适用于二次元 LoRA 训练。",
            Enabled = true, Sort = 35, OutputFormat = "danbooru_tags",
            Tags = new List<string> { "Danbooru", "动漫", "训练" },
            SubjectDomains = new List<string> { "general", "portrait" },
        },
        new()
        {
            Id = "pe_train_e621", Kind = "train", Builtin = true, BuiltinKey = "e621_tag_list",
            Name = "e621 标签列表（兽系）", Category = "train",
            Description = "一行 e621 风格英文标签（含 artist:/species:/lore: 等前缀），适用于兽系素材训练。",
            Enabled = true, Sort = 40, OutputFormat = "danbooru_tags",
            Tags = new List<string> { "e621", "兽系", "训练" },
            SubjectDomains = new List<string> { "general", "animal", "portrait" },
        },
        new()
        {
            Id = "pe_train_rule34", Kind = "train", Builtin = true, BuiltinKey = "Rule34_tag_list",
            Name = "Rule34 标签列表", Category = "train",
            Description = "一行 Rule34 风格英文标签（artist:/copyright:/character:/meta: 前缀 + 通用标签）。",
            Enabled = true, Sort = 45, OutputFormat = "danbooru_tags",
            Tags = new List<string> { "Rule34", "Booru", "训练" },
            SubjectDomains = new List<string> { "general", "portrait" },
        },
        new()
        {
            Id = "pe_train_booru", Kind = "train", Builtin = true, BuiltinKey = "Booru_tag_list",
            Name = "Booru 风格标签列表", Category = "train",
            Description = "一行 Booru 类站点风格的英文标签列表，逗号分隔。",
            Enabled = true, Sort = 50, OutputFormat = "danbooru_tags",
            Tags = new List<string> { "Booru", "标签", "训练" },
            SubjectDomains = new List<string> { "general", "portrait" },
        },
        new()
        {
            Id = "pe_train_art_critic", Kind = "train", Builtin = true, BuiltinKey = "Art_Critic",
            Name = "艺术评论式描述", Category = "train",
            Description = "从艺术评论角度描述构图、风格、象征、色彩、光线与艺术流派等。",
            Enabled = true, Sort = 55, OutputFormat = "prose",
            Tags = new List<string> { "艺术评论", "自然语言", "训练" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
        new()
        {
            Id = "pe_train_product_listing", Kind = "train", Builtin = true, BuiltinKey = "Product_Listing",
            Name = "商品详情描述", Category = "train",
            Description = "以电商商品详情页的口吻描述画面中的产品、材质与卖点。",
            Enabled = true, Sort = 60, OutputFormat = "prose",
            Tags = new List<string> { "商品", "自然语言", "训练" },
            SubjectDomains = new List<string> { "general", "product", "still_life" },
        },
        new()
        {
            Id = "pe_train_social_media_post", Kind = "train", Builtin = true, BuiltinKey = "Social_Media_Post",
            Name = "社交媒体文案", Category = "train",
            Description = "以社交媒体发帖风格撰写配图文案，语气贴近日常分享。",
            Enabled = true, Sort = 65, OutputFormat = "prose",
            Tags = new List<string> { "社媒", "自然语言", "训练" },
            SubjectDomains = new List<string> { "general", "portrait", "landscape", "architecture", "animal", "product", "still_life" },
        },
    };

    /// <summary>默认内置 PE 全集（10 minimax 扩写 + 13 扩写镜像 + 3 反推 + 12 训练打标 + 10 torii；按 sort 升序）。
    /// 对齐提示词大师 promptEngineeringRegistry.js getDefaultBuiltinProfiles：minimaxScenarios + expandProfiles + reverse + train + torii。</summary>
    public static List<PromptEngineeringProfile> GetDefaultBuiltinProfiles()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<PromptEngineeringProfile>();
        list.AddRange(BuildMinimaxExpandProfiles());
        list.AddRange(BuiltinReverseProfiles.Select(MirrorForExpand));
        list.AddRange(ToriiGateFormats.BuildToriiReverseProfiles().Select(MirrorForExpand));
        list.AddRange(BuiltinReverseProfiles);
        list.AddRange(BuiltinTrainProfiles);
        list.AddRange(ToriiGateFormats.BuildToriiReverseProfiles());
        foreach (var p in list)
        {
            if (p.CreatedAt == 0)
                p.CreatedAt = now;
            if (p.UpdatedAt == 0)
                p.UpdatedAt = now;
        }
        return list.OrderBy(p => p.Sort).ThenBy(p => p.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true)).ToList();
    }

    /// <summary>扩写镜像（expandReverseMirror.js mirrorProfileForExpand）：反推/torii → 扩写同款条目。
    /// id 前缀替换：pe_reverse_ → pe_expand_；pe_torii_ → pe_expand_torii_；其余 pe_ → pe_expand_。
    /// kind=expand、category=扩写；systemPrompt/userPromptTemplate 清空（内置扩写提示词由运行时组装）。</summary>
    public static PromptEngineeringProfile MirrorForExpand(PromptEngineeringProfile src)
    {
        var id = src.Id ?? "";
        if (id.StartsWith("pe_reverse_", StringComparison.Ordinal))
            id = "pe_expand_" + id["pe_reverse_".Length..];
        else if (id.StartsWith("pe_torii_", StringComparison.Ordinal))
            id = "pe_expand_torii_" + id["pe_torii_".Length..];
        else if (id.Length > 0 && !id.StartsWith("pe_expand_", StringComparison.Ordinal))
            id = "pe_expand_" + (id.StartsWith("pe_", StringComparison.Ordinal) ? id[3..] : id);
        return new PromptEngineeringProfile
        {
            Id = id,
            Kind = "expand",
            Builtin = true,
            BuiltinKey = src.BuiltinKey,
            CaptionType = src.CaptionType,
            StructuredFormat = src.StructuredFormat,
            ToriiFormat = src.ToriiFormat,
            ToriiExtractMode = src.ToriiExtractMode,
            ToriiUseNamesDefault = src.ToriiUseNamesDefault,
            Name = src.Name,
            Category = "扩写",
            Description = src.Description,
            Enabled = src.Enabled,
            Sort = src.Sort,
            OutputFormat = src.OutputFormat,
            Tags = new List<string>(src.Tags ?? new List<string>()),
            SubjectDomains = new List<string>(src.SubjectDomains ?? new List<string>()),
            SystemPrompt = "",
            UserPromptTemplate = "",
        };
    }

    /// <summary>MiniMax 场景 → 扩写工程条目（minimaxScenarios buildMinimaxScenarioProfiles）。
    /// id=场景 PeId（pe_expand_*）、kind=expand、OutputFormat=minimax（表格显示 MiniMax H3）。</summary>
    public static List<PromptEngineeringProfile> BuildMinimaxExpandProfiles()
    {
        return MiniMaxScenarios.All
            .Select(s => new PromptEngineeringProfile
            {
                Id = s.PeId,
                Kind = "expand",
                Builtin = true,
                BuiltinKey = s.BuiltinKey,
                Name = s.Name,
                Category = "扩写",
                Description = s.Description,
                Enabled = true,
                Sort = s.Sort,
                OutputFormat = "minimax",
                Tags = new List<string> { "MiniMax H3" },
                SubjectDomains = new List<string> { "general" },
                SystemPrompt = "",
                UserPromptTemplate = "",
            })
            .ToList();
    }
}

public sealed class PmPromptEngineeringService : IPmPromptEngineeringService
{
    private const string PeExportFormat = "prompt-master-prompt-engineering";

    public const string DefaultReverseUserTemplate = "{{task_lead}}\n\n{{length_block}}\n\n{{extra_block}}";
    public const string DefaultExpandUserTemplate = "{{intro}}\n\n{{lang_lock}}\n\n【{{length_title}}】{{length_hint}}\n\n{{format_block}}\n\n{{extra_block}}\n\n【{{user_input_title}}】\n{{user_input}}";

    private readonly IBaseLogService _log;
    private readonly string _workspaceRoot;
    private readonly IDbContextFactory<ComfyDbContext>? _dbFactory;

    /// <summary>
    /// 构造。传入 <paramref name="dbFactory"/> 后自定义 PE 持久化到 comfyui.db（pe_profiles 表）；
    /// 不传时回退到旧 JSON 文件（workspace/promptmaster/data/prompt_engineering.json）兼容旧调用点。
    /// </summary>
    public PmPromptEngineeringService(IBaseLogService? log = null, string? workspaceRoot = null, IDbContextFactory<ComfyDbContext>? dbFactory = null)
    {
        _log = log ?? PromptCraft.Service.LogService.Instance;
        _workspaceRoot = workspaceRoot ?? ResolveWorkspaceRoot();
        _dbFactory = dbFactory;
    }

    private static string ResolveWorkspaceRoot()
    {
        try
        {
            var svc = new PromptCraft.Service.WorkspaceService();
            return svc.Root ?? PromptCraft.Service.WorkspaceService.DefaultRoot;
        }
        catch
        {
            return PromptCraft.Service.WorkspaceService.DefaultRoot;
        }
    }

    private string PeFile()
        => Path.Combine(_workspaceRoot, "promptmaster", "data", "prompt_engineering.json");

    private static string NormalizeString(string? v) => (v ?? "").Trim();

    // ==================== 注册表 ====================

    public static PromptEngineeringProfile NormalizeProfile(PromptEngineeringProfile? raw)
    {
        var p = raw ?? new PromptEngineeringProfile();
        var kind = p.Kind is "reverse" ? "reverse" : p.Kind is "train" ? "train" : "expand";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new PromptEngineeringProfile
        {
            Id = NormalizeString(p.Id).Length > 0 ? NormalizeString(p.Id) : $"pe_custom_{Guid.NewGuid():N}",
            Kind = kind,
            Builtin = p.Builtin,
            BuiltinKey = p.BuiltinKey ?? "",
            Name = NormalizeString(p.Name).Length > 0 ? NormalizeString(p.Name) : Localizer.Instance?["PeSvcUnnamed"] ?? "",
            Category = NormalizeString(p.Category).Length > 0 ? NormalizeString(p.Category) : kind,
            Description = NormalizeString(p.Description),
            SystemPrompt = p.SystemPrompt ?? "",
            UserPromptTemplate = p.UserPromptTemplate ?? "",
            Enabled = p.Enabled,
            Sort = p.Sort,
            CreatedAt = p.CreatedAt > 0 ? p.CreatedAt : now,
            UpdatedAt = p.UpdatedAt > 0 ? p.UpdatedAt : now,
            ToriiFormat = p.ToriiFormat,
            ToriiExtractMode = p.ToriiExtractMode ?? "",
            ToriiUseNamesDefault = p.ToriiUseNamesDefault ?? true,
            OutputFormat = NormalizeString(p.OutputFormat),
            Tags = p.Tags ?? new List<string>(),
            SubjectDomains = p.SubjectDomains ?? new List<string>(),
        };
        return EnrichProfileTaxonomy(result);
    }

    /// <summary>内置 PE 备注仅以代码 registry 为准（不读持久化 description）。</summary>
    private static string BuiltinRegistryDescription(PromptEngineeringProfile b)
        => NormalizeString(b.Description);

    private static List<PromptEngineeringProfile> MergeWithBuiltins(
        IEnumerable<PromptEngineeringProfile>? profiles,
        IEnumerable<(string Id, bool Enabled, int Sort)>? builtinOverrides)
    {
        var builtins = PromptEngineeringRegistry.GetDefaultBuiltinProfiles();
        var builtinIds = new HashSet<string>(builtins.Select(b => b.Id), StringComparer.Ordinal);

        var customMap = new Dictionary<string, PromptEngineeringProfile>(StringComparer.Ordinal);
        foreach (var p in profiles ?? new List<PromptEngineeringProfile>())
        {
            if (builtinIds.Contains(p.Id) || p.Builtin)
                continue;
            customMap[p.Id] = NormalizeProfile(p);
        }

        var overrideMap = new Dictionary<string, (string Id, bool Enabled, int Sort)>(StringComparer.Ordinal);
        foreach (var o in builtinOverrides ?? new List<(string Id, bool Enabled, int Sort)>())
            overrideMap[o.Id] = o;

        var byId = new List<PromptEngineeringProfile>();
        foreach (var b in builtins)
        {
            var hasOv = overrideMap.TryGetValue(b.Id, out var ov);
            var copy = new PromptEngineeringProfile
            {
                Id = b.Id,
                Kind = b.Kind,
                Builtin = true,
                BuiltinKey = b.BuiltinKey,
                Name = b.Name,
                Category = b.Category,
                Description = BuiltinRegistryDescription(b),
                Enabled = hasOv ? ov.Enabled : b.Enabled,
                Sort = hasOv ? ov.Sort : b.Sort,
                CreatedAt = b.CreatedAt,
                UpdatedAt = b.UpdatedAt,
                StructuredFormat = b.StructuredFormat,
                ToriiFormat = b.ToriiFormat,
                ToriiExtractMode = b.ToriiExtractMode,
                ToriiUseNamesDefault = b.ToriiUseNamesDefault,
                OutputFormat = b.OutputFormat,
                Tags = new List<string>(b.Tags ?? new List<string>()),
                SubjectDomains = new List<string>(b.SubjectDomains ?? new List<string>()),
            };
            byId.Add(NormalizeProfile(copy));
        }
        foreach (var (id, p) in customMap)
        {
            if (byId.All(x => x.Id != id))
                byId.Add(p);
        }
        return byId
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true))
            .ToList();
    }

    // ==================== 持久化 ====================

    /// <summary>读存储：优先 comfyui.db（pe_profiles），无 dbFactory 时回退 JSON 文件。</summary>
    private (List<PromptEngineeringProfile> Profiles, List<PromptEngineeringProfile> BuiltinOverrides) LoadStore()
        => _dbFactory != null ? LoadStoreFromDb() : LoadStoreFromFile();

    /// <summary>EF 版读存储。首次使用时若 db 为空且旧 JSON 存在，一次性迁移进 comfyui.db。</summary>
    private (List<PromptEngineeringProfile> Profiles, List<PromptEngineeringProfile> BuiltinOverrides) LoadStoreFromDb()
    {
        List<PromptEngineeringProfile> dbRows;
        using (var db = _dbFactory!.CreateDbContext())
        {
            dbRows = db.PromptEngineeringProfiles.AsNoTracking().ToList();
        }

        // 首次迁移：db 无数据但旧 JSON 存在 → 导入后落库
        if (dbRows.Count == 0 && File.Exists(PeFile()))
        {
            try
            {
                var (fileProfiles, fileOverrides) = ReadPeFile();
                if (fileProfiles.Count > 0 || fileOverrides.Count > 0)
                {
                    SaveStoreToDb(fileProfiles.Concat(fileOverrides));
                    using var db2 = _dbFactory.CreateDbContext();
                    dbRows = db2.PromptEngineeringProfiles.AsNoTracking().ToList();
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[pm_prompt_engineering] json migrate failed: {ex.Message}", "PromptEngineering");
            }
        }

        // 清理过期内置元数据（内置注册表代码内维护，表只落 customOnly 与 builtinOverrides 两段）
        var builtinIds = new HashSet<string>(PromptEngineeringRegistry.GetDefaultBuiltinProfiles().Select(p => p.Id), StringComparer.Ordinal);
        var dirty = false;
        var cleanProfiles = new List<PromptEngineeringProfile>();
        var cleanOverrides = new List<PromptEngineeringProfile>();
        foreach (var row in dbRows)
        {
            if (row.Builtin)
            {
                if (row.Id.StartsWith("pe_train_torii_", StringComparison.Ordinal))
                {
                    dirty = true;
                    continue;
                }
                cleanOverrides.Add(new PromptEngineeringProfile
                {
                    Id = row.Id,
                    Enabled = row.Enabled,
                    Sort = row.Sort,
                });
                continue;
            }
            if (builtinIds.Contains(row.Id))
            {
                dirty = true;
                continue;
            }
            cleanProfiles.Add(NormalizeProfile(row));
        }

        var merged = MergeWithBuiltins(cleanProfiles, cleanOverrides.Select(o => (o.Id, o.Enabled, o.Sort)));
        if (merged.Count == 0)
            merged = MergeWithBuiltins(new List<PromptEngineeringProfile>(), new List<(string Id, bool Enabled, int Sort)>());
        if (dirty)
            SaveStore(merged);
        return (merged, cleanOverrides);
    }

    /// <summary>JSON 版读存储（无 dbFactory 时的回退；保留旧逻辑）。</summary>
    private (List<PromptEngineeringProfile> Profiles, List<PromptEngineeringProfile> BuiltinOverrides) LoadStoreFromFile()
    {
        var (profiles, builtinOverrides) = ReadPeFile();

        // 清理过期内置元数据
        var builtinIds = new HashSet<string>(PromptEngineeringRegistry.GetDefaultBuiltinProfiles().Select(p => p.Id), StringComparer.Ordinal);
        var dirty = false;
        var cleanProfiles = new List<PromptEngineeringProfile>();
        foreach (var p in profiles)
        {
            if (builtinIds.Contains(p.Id) || p.Builtin)
            {
                dirty = true;
                continue;
            }
            cleanProfiles.Add(p);
        }
        var cleanOverrides = new List<PromptEngineeringProfile>();
        foreach (var o in builtinOverrides)
        {
            if (o.Id.StartsWith("pe_train_torii_", StringComparison.Ordinal))
            {
                dirty = true;
                continue;
            }
            cleanOverrides.Add(new PromptEngineeringProfile
            {
                Id = o.Id,
                Enabled = o.Enabled,
                Sort = o.Sort,
            });
        }

        var merged = MergeWithBuiltins(cleanProfiles, cleanOverrides.Select(o => (o.Id, o.Enabled, o.Sort)));
        if (merged.Count == 0)
            merged = MergeWithBuiltins(new List<PromptEngineeringProfile>(), new List<(string Id, bool Enabled, int Sort)>());
        if (dirty || !File.Exists(PeFile()))
            SaveStore(merged);
        return (merged, cleanOverrides);
    }

    /// <summary>仅读 JSON 文件原始内容（profiles / builtinOverrides），不做清理与合并。</summary>
    private (List<PromptEngineeringProfile> Profiles, List<PromptEngineeringProfile> BuiltinOverrides) ReadPeFile()
    {
        var profiles = new List<PromptEngineeringProfile>();
        var builtinOverrides = new List<PromptEngineeringProfile>();
        var file = PeFile();
        if (File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("profiles", out var arr))
                {
                    foreach (var el in arr.EnumerateArray())
                        profiles.Add(el.Deserialize<PromptEngineeringProfile>(PeJsonOptions)!);
                }
                if (root.TryGetProperty("builtinOverrides", out var arr2))
                {
                    foreach (var el in arr2.EnumerateArray())
                        builtinOverrides.Add(el.Deserialize<PromptEngineeringProfile>(PeJsonOptions)!);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[pm_prompt_engineering] parse failed: {ex.Message}", "PromptEngineering");
            }
        }
        return (profiles, builtinOverrides);
    }

    public void SaveStore(IEnumerable<PromptEngineeringProfile> profiles)
    {
        if (_dbFactory != null)
        {
            SaveStoreToDb(profiles);
            return;
        }
        SaveStoreToFile(profiles);
    }

    /// <summary>EF 版写存储：全量替换（customOnly 全字段 + builtinOverrides 两段），与 JSON 版语义一致。</summary>
    private void SaveStoreToDb(IEnumerable<PromptEngineeringProfile> profiles)
    {
        var list = profiles.ToList();
        var customOnly = list
            .Where(p => !p.Builtin)
            .Select(NormalizeProfile)
            .ToList();
        var builtinOverrides = list
            .Where(p => p.Builtin)
            .Select(p => new PromptEngineeringProfile { Id = p.Id, Builtin = true, Enabled = p.Enabled, Sort = p.Sort })
            .ToList();
        using var db = _dbFactory!.CreateDbContext();
        db.PromptEngineeringProfiles.RemoveRange(db.PromptEngineeringProfiles);
        foreach (var p in customOnly)
            db.PromptEngineeringProfiles.Add(p);
        foreach (var o in builtinOverrides)
            db.PromptEngineeringProfiles.Add(o);
        db.SaveChanges();
    }

    /// <summary>JSON 版写存储（无 dbFactory 时的回退；保留旧逻辑）。</summary>
    private void SaveStoreToFile(IEnumerable<PromptEngineeringProfile> profiles)
    {
        var list = profiles.ToList();
        var dir = Path.GetDirectoryName(PeFile());
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var customOnly = list
            .Where(p => !p.Builtin)
            .Select(p => new
            {
                id = p.Id,
                kind = p.Kind,
                name = p.Name,
                category = p.Category,
                description = p.Description,
                systemPrompt = p.SystemPrompt,
                userPromptTemplate = p.UserPromptTemplate,
                outputFormat = p.OutputFormat,
                tags = p.Tags,
                subjectDomains = p.SubjectDomains,
                enabled = p.Enabled,
                sort = p.Sort,
                createdAt = p.CreatedAt,
                updatedAt = p.UpdatedAt,
            })
            .ToList();
        var builtinOverrides = list
            .Where(p => p.Builtin)
            .Select(p => new { id = p.Id, enabled = p.Enabled, sort = p.Sort })
            .ToList();
        var payload = new
        {
            version = 1,
            profiles = customOnly,
            builtinOverrides,
        };
        File.WriteAllText(PeFile(), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static readonly JsonSerializerOptions PeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ==================== 查询 / CRUD ====================

    public List<PromptEngineeringProfile> ListProfiles(string? kind = null, bool enabledOnly = false)
    {
        var (profiles, _) = LoadStore();
        var outList = profiles.AsEnumerable();
        if (!string.IsNullOrEmpty(kind))
            outList = outList.Where(p => p.Kind == kind);
        if (enabledOnly)
            outList = outList.Where(p => p.Enabled);
        return outList.ToList();
    }

    public PromptEngineeringProfile? GetProfile(string? id)
    {
        var key = NormalizeString(id);
        if (key.Length == 0)
            return null;
        var (profiles, _) = LoadStore();
        var hit = profiles.FirstOrDefault(p => p.Id == key);
        if (hit != null)
            return hit;
        return PromptEngineeringRegistry.GetDefaultBuiltinProfiles().FirstOrDefault(p => p.Id == key);
    }

    public PromptEngineeringProfile? SaveProfile(PromptEngineeringProfile profile)
    {
        var incoming = NormalizeProfile(profile);
        if (incoming.Builtin)
            throw new InvalidOperationException(Localizer.Instance?["PeSvcBuiltinNotEditable"] ?? "");
        if (NormalizeString(incoming.Name).Length == 0)
            throw new InvalidOperationException(Localizer.Instance?["PeNameRequired"] ?? "");
        if (NormalizeString(incoming.SystemPrompt).Length == 0)
            throw new InvalidOperationException(Localizer.Instance?["PeSystemPromptRequired"] ?? "");
        if (!incoming.Builtin)
        {
            var fmt = NormalizeString(incoming.OutputFormat);
            if (fmt.Length > 0 && !OutputFormats.Any(f => f == fmt))
                throw new InvalidOperationException(Localizer.Instance?["PeSvcInvalidOutputFormat"] ?? "");
            if (fmt.Length == 0)
                incoming.OutputFormat = InferOutputFormat(incoming);
            incoming.Tags = InferTags(incoming);
            incoming.SubjectDomains = InferredSubjectDomains(incoming);
        }

        var (profiles, _) = LoadStore();
        var idx = profiles.FindIndex(p => p.Id == incoming.Id);
        incoming.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (idx >= 0 && !profiles[idx].Builtin)
        {
            incoming.CreatedAt = profiles[idx].CreatedAt;
            profiles[idx] = incoming;
        }
        else
        {
            if (!incoming.Id.StartsWith("pe_custom_", StringComparison.Ordinal))
                incoming.Id = $"pe_custom_{Guid.NewGuid():N}";
            incoming.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            profiles.Add(incoming);
        }
        SaveStore(profiles);
        return incoming;
    }

    public void SetProfileEnabled(string? id, bool enabled)
    {
        var (profiles, _) = LoadStore();
        var p = profiles.FirstOrDefault(x => x.Id == id);
        if (p == null)
            throw new InvalidOperationException(Localizer.Instance?["PeNotFound"] ?? "");
        p.Enabled = enabled;
        p.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SaveStore(profiles);
    }

    public void DeleteProfile(string? id)
    {
        var (profiles, _) = LoadStore();
        var p = profiles.FirstOrDefault(x => x.Id == id);
        if (p == null)
            throw new InvalidOperationException(Localizer.Instance?["PeNotFound"] ?? "");
        if (p.Builtin)
            throw new InvalidOperationException(Localizer.Instance?["PeSvcBuiltinNotDeletable"] ?? "");
        SaveStore(profiles.Where(x => x.Id != id));
    }

    // ==================== 导出 / 导入（对齐 pm_prompt_engineering.js exportProfile / validateImportFile / importProfilesFromPath） ====================

    /// <summary>导出单条工程（内置拒绝）。返回导出 payload JSON 字符串，文件保存由 UI 负责。</summary>
    public string ExportProfile(string? id)
    {
        var key = NormalizeString(id);
        if (key.Length == 0)
            throw new InvalidOperationException(Localizer.Instance?["PeSvcNoExportTarget"] ?? "");
        var p = GetProfile(key);
        if (p == null)
            throw new InvalidOperationException(Localizer.Instance?["PeNotFound"] ?? "");
        if (p.Builtin)
            throw new InvalidOperationException(Localizer.Instance?["PeSvcBuiltinNotExportable"] ?? "");
        var payload = new
        {
            format = PeExportFormat,
            version = 1,
            exportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            profile = ProfileToExportItem(p),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>导出单条字段（profileToExportItem）。</summary>
    private static object ProfileToExportItem(PromptEngineeringProfile p) => new
    {
        kind = p.Kind,
        name = p.Name,
        category = p.Category,
        description = p.Description,
        systemPrompt = p.SystemPrompt,
        userPromptTemplate = p.UserPromptTemplate,
        outputFormat = p.OutputFormat,
        tags = p.Tags,
        subjectDomains = p.SubjectDomains,
        enabled = p.Enabled,
        sort = p.Sort,
    };

    /// <summary>校验导入文件（validateImportFile）。返回可导入状态与预览信息。</summary>
    public (bool Valid, string FileName, string? ProfileName, string? ProfileKind, string Message) ValidateImportFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (false, "", null, null, Localizer.Instance?["PeSvcNoFile"] ?? "");
        var fileName = Path.GetFileName(filePath);
        if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return (false, fileName, null, null, Localizer.Instance?["PeSvcPickJson"] ?? "");
        if (!File.Exists(filePath))
            return (false, fileName, null, null, Localizer.Instance?["PeSvcFileMissing"] ?? "");
        JsonElement raw;
        try
        {
            raw = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(filePath));
        }
        catch
        {
            return (false, fileName, null, null, Localizer.Instance?["PeSvcJsonParseFailed"] ?? "");
        }

        var (valid, message, profiles) = ValidatePeImportPayload(raw);
        if (!valid)
            return (false, fileName, null, null, message);
        var builtinIds = new HashSet<string>(PromptEngineeringRegistry.GetDefaultBuiltinProfiles().Select(p => p.Id), StringComparer.Ordinal);
        var item = PickSingleImportableProfile(profiles, builtinIds);
        var preview = profiles.FirstOrDefault();
        var kindLabel = JStr(preview, "kind") == "reverse" ? (Localizer.Instance?["KindReverse"] ?? "")
            : JStr(preview, "kind") == "train" ? (Localizer.Instance?["KindTrain"] ?? "")
            : (Localizer.Instance?["KindExpand"] ?? "");
        return item.ValueKind == JsonValueKind.Undefined
            ? (false, fileName, JStr(preview, "name"), JStr(preview, "kind"), Localizer.Instance?["PeSvcPreviewBuiltinNotImportable"] ?? "")
            : (true, fileName, JStr(preview, "name"), JStr(preview, "kind"), string.Format(Localizer.Instance?["PeSvcPreviewImportableFormat"] ?? "", kindLabel, JStr(item, "name")));
    }

    /// <summary>导入单条工程（importProfilesFromPath）。返回已入库的工程。</summary>
    public PromptEngineeringProfile ImportProfile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localizer.Instance?["PeSvcPickJson"] ?? "");
        if (!File.Exists(filePath))
            throw new InvalidOperationException(Localizer.Instance?["PeSvcFileMissing"] ?? "");
        JsonElement raw;
        try
        {
            raw = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(filePath));
        }
        catch
        {
            throw new InvalidOperationException(Localizer.Instance?["PeSvcJsonParseFailed"] ?? "");
        }

        var (valid, message, profiles) = ValidatePeImportPayload(raw);
        if (!valid)
            throw new InvalidOperationException(message);
        var builtinIds = new HashSet<string>(PromptEngineeringRegistry.GetDefaultBuiltinProfiles().Select(p => p.Id), StringComparer.Ordinal);
        var item = PickSingleImportableProfile(profiles, builtinIds);
        if (item.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException(Localizer.Instance?["PeSvcBuiltinNotImportable"] ?? "");

        var parsed = item.Deserialize<PromptEngineeringProfile>(PeJsonOptions) ?? new PromptEngineeringProfile();
        var kind = parsed.Kind == "reverse" ? "reverse" : parsed.Kind == "train" ? "train" : "expand";
        var incoming = NormalizeProfile(new PromptEngineeringProfile
        {
            Kind = kind,
            Builtin = false,
            BuiltinKey = "",
            Name = parsed.Name,
            Category = parsed.Category,
            Description = parsed.Description,
            SystemPrompt = parsed.SystemPrompt,
            UserPromptTemplate = parsed.UserPromptTemplate,
            OutputFormat = parsed.OutputFormat,
            Tags = parsed.Tags ?? new List<string>(),
            SubjectDomains = parsed.SubjectDomains ?? new List<string>(),
            Enabled = parsed.Enabled,
            Sort = parsed.Sort,
        });
        if (incoming.Name == "未命名")
            throw new InvalidOperationException(Localizer.Instance?["PeSvcProfileNoName"] ?? "");
        if (string.IsNullOrWhiteSpace(incoming.SystemPrompt))
            throw new InvalidOperationException(Localizer.Instance?["PeSvcProfileNoSystemPrompt"] ?? "");
        incoming.Id = $"pe_custom_{Guid.NewGuid():N}";
        incoming.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        incoming.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (profilesList, _) = LoadStore();
        profilesList.Add(incoming);
        SaveStore(profilesList);
        return incoming;
    }

    /// <summary>校验导入 payload（validatePeImportPayload）。支持单条导出 / 旧格式 / 裸对象三种形态。</summary>
    private static (bool Valid, string Message, List<JsonElement> Profiles) ValidatePeImportPayload(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            return (false, Localizer.Instance?["PeSvcInvalidJsonFile"] ?? "", new List<JsonElement>());
        var format = JStr(raw, "format");
        if (format.Length > 0 && format != PeExportFormat)
            return (false, Localizer.Instance?["PeSvcNotExportFile"] ?? "", new List<JsonElement>());
        if (format == PeExportFormat)
        {
            if (raw.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
            {
                if (!IsPeProfileLike(profile))
                    return (false, Localizer.Instance?["PeSvcInvalidProfile"] ?? "", new List<JsonElement>());
                return (true, "", new List<JsonElement> { profile });
            }
            if (raw.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            {
                var arr = profiles.EnumerateArray().ToList();
                if (arr.Count > 1)
                    return (false, Localizer.Instance?["PeSvcMultipleProfiles"] ?? "", new List<JsonElement>());
                if (arr.Count == 0 || !IsPeProfileLike(arr[0]))
                    return (false, Localizer.Instance?["PeSvcNoEntries"] ?? "", new List<JsonElement>());
                return (true, "", new List<JsonElement> { arr[0] });
            }
            return (false, Localizer.Instance?["PeSvcMissingProfileField"] ?? "", new List<JsonElement>());
        }
        if (IsPeProfileLike(raw) && (JStr(raw, "kind") == "expand" || JStr(raw, "kind") == "reverse" || JStr(raw, "kind") == "train"))
            return (true, "", new List<JsonElement> { raw });

        var items = ParseImportPayload(raw);
        if (items.Count == 0)
            return (false, Localizer.Instance?["PeSvcNotPeJson"] ?? "", new List<JsonElement>());
        var validItems = items.Where(IsPeProfileLike).ToList();
        if (validItems.Count == 0)
            return (false, Localizer.Instance?["PeSvcNoValidProfile"] ?? "", new List<JsonElement>());
        if (validItems.Count > 1)
            return (false, Localizer.Instance?["PeSvcMultipleProfiles"] ?? "", new List<JsonElement>());
        if (raw.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number
            && ver.TryGetInt32(out var v) && v == 1
            && raw.TryGetProperty("builtinOverrides", out var ov) && ov.ValueKind == JsonValueKind.Array)
            return (false, Localizer.Instance?["PeSvcWorkspaceMultiple"] ?? "", new List<JsonElement>());
        return (true, "", new List<JsonElement> { validItems[0] });
    }

    /// <summary>提取可导入的单条工程（pickSingleImportableProfile）：非内置且不在内置 id 集合。</summary>
    private static JsonElement PickSingleImportableProfile(List<JsonElement> items, HashSet<string> builtinIds)
    {
        var validItems = (items ?? new List<JsonElement>()).Where(IsPeProfileLike).ToList();
        if (validItems.Count != 1)
            return default;
        var item = validItems[0];
        if (JBool(item, "builtin", false))
            return default;
        var id = JStr(item, "id");
        if (id.Length > 0 && builtinIds.Contains(id))
            return default;
        return item;
    }

    private static List<JsonElement> ParseImportPayload(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Array)
            return raw.EnumerateArray().ToList();
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (raw.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
                return new List<JsonElement> { profile };
            if (raw.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                return profiles.EnumerateArray().ToList();
        }
        return new List<JsonElement>();
    }

    /// <summary>是否可导入的工程条目（isPeProfileLike）：非内置、kind 合法、有名称与系统提示词。</summary>
    private static bool IsPeProfileLike(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return false;
        if (JBool(item, "builtin", false))
            return false;
        var kind = JStr(item, "kind");
        if (kind != "expand" && kind != "reverse" && kind != "train")
            return false;
        var hasName = JStr(item, "name").Trim().Length > 0;
        var hasSystem = JStr(item, "systemPrompt").Trim().Length > 0;
        return hasName && hasSystem;
    }

    private static string JStr(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static bool JBool(JsonElement e, string prop, bool def = false)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind == JsonValueKind.True
            : def;

    // ==================== applyReverseToCaption（核心映射） ====================

    /// <summary>
    /// 把所选 PE 映射到 caption（pm_prompt_engineering.js applyReverseToCaption）：
    /// torii 结构化工程 → type=Structured + torii 状态注入；内置/自定义工程 → type + pe_builtin / pe_custom_*。
    /// </summary>
    public ReverseCaptionRequest ApplyReverseToCaption(ReverseCaptionRequest caption, string? peId)
    {
        var profile = GetProfile(peId);
        if (profile == null || (profile.Kind != "reverse" && profile.Kind != "train"))
            return caption;
        if (!profile.Enabled)
            return caption;

        // torii 结构化模板
        if (ToriiGateFormats.IsStructuredTemplateProfile(profile))
        {
            var extractMode = NormalizeString(profile.ToriiExtractMode);
            if (extractMode.Length == 0)
                extractMode = "full";
            var jsonFmtKeys = new HashSet<string> { "json", "min_structured_json", "json_comic" };
            if (jsonFmtKeys.Contains(profile.BuiltinKey) && extractMode == "json_flat")
                extractMode = "json_raw";
            var useNames = caption.ToriiUseNames != null
                ? caption.ToriiUseNames.Value
                : (profile.ToriiUseNamesDefault ?? true);

            caption.Type = "Structured";
            caption.PeOutputFormatValue = InferOutputFormat(profile);
            caption.PeId = profile.Id;
            caption.PeBuiltin = true;
            caption.StructuredFormatValue = profile.BuiltinKey;
            caption.ToriiFormatValue = profile.BuiltinKey;
            caption.ToriiExtractModeValue = extractMode;
            caption.ToriiUseNames = useNames;
            caption.ToriiAddTags = caption.ToriiAddTags;
            caption.ToriiGroundingTags = caption.ToriiGroundingTags ?? "";
            caption.ToriiGroundingCharacters = caption.ToriiGroundingCharacters ?? "";
            caption.Anima3Enhance = false;
            return caption;
        }

        // 内置 / 自定义
        var resolved = ResolveReversePrompts(profile, caption);
        caption.Type = resolved.CaptionType;
        caption.PeOutputFormatValue = InferOutputFormat(profile);
        caption.PeId = profile.Id;
        caption.PeBuiltin = resolved.Builtin;
        if (!resolved.Builtin)
        {
            caption.PeCustomSystem = resolved.System;
            caption.PeCustomUserBody = resolved.UserBody;
            caption.PeCustomOutputConstraints = resolved.OutputConstraints;
        }
        return caption;
    }

    /// <summary>兼容旧 caption.type → pe id（mapLegacyCaptionType）。</summary>
    public static string MapLegacyCaptionType(string type)
        => type switch
        {
            "Descriptive" => "pe_reverse_descriptive",
            "Danbooru_tag_list" => "pe_reverse_danbooru",
            _ => "pe_reverse_sd",
        };

    // ==================== resolver（promptEngineeringResolver.js 反推侧） ====================

    private static string RenderTemplate(string tpl, IReadOnlyDictionary<string, string> vars)
    {
        var s = tpl ?? "";
        foreach (var (k, v) in vars)
            s = s.Replace($"{{{{{k}}}}}", v ?? "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string BlockOrEmpty(string? line)
    {
        var t = (line ?? "").Trim();
        return t.Length > 0 ? $"{t}\n\n" : "";
    }

    public static string CaptionTypeFromProfile(PromptEngineeringProfile profile)
        => profile.Builtin && !string.IsNullOrEmpty(profile.BuiltinKey)
            ? profile.BuiltinKey
            : string.IsNullOrEmpty(profile.CaptionType) ? "Stable_Diffusion_Prompt" : profile.CaptionType;

    /// <summary>反推提示词解析（resolveReversePrompts）。</summary>
    public static ReverseResolvedPrompts ResolveReversePrompts(PromptEngineeringProfile profile, ReverseCaptionRequest caption)
    {
        var mt = caption.MediaTarget ?? "image";
        var capType = CaptionTypeFromProfile(profile);

        if (profile.Builtin)
        {
            return new ReverseResolvedPrompts
            {
                CaptionType = capType,
                Builtin = true,
                System = CaptionPromptBlocks.BuildSystemPrompt(caption, mt) + CaptionPromptBlocks.BuildSystemAddons(caption, mt),
                UserLead = CaptionPromptBlocks.BuildUserTaskLead(caption, mt),
                UserBody = CaptionPromptBlocks.BuildUserTaskBody(caption) ?? "",
                OutputConstraints = CaptionPromptBlocks.BuildOutputConstraints(caption),
                UserTail = CaptionPromptBlocks.BuildUserTailAddon(caption),
            };
        }

        caption.CaptionLang ??= "en";
        var zh = caption.CaptionLang == "zh";
        var system = NormalizeString(profile.SystemPrompt);
        system = ApplyOutputLanguage(system, caption.CaptionLang);
        system += CaptionLength.BuildCaptionLengthBlock(caption);
        system += JoyCaptionExtraPromptEngineering.BuildJoyExtraSystemEnforcementBlock(caption, mt);

        var taskLead = zh
            ? "请根据当前媒体内容，严格按系统提示词要求输出反推结果。只输出正文，不要解释。"
            : "Analyze the media and output the caption per system instructions. Output body only.";

        var userTpl = NormalizeString(profile.UserPromptTemplate);
        if (userTpl.Length == 0)
            userTpl = DefaultReverseUserTemplate;
        var ep = (caption.ExtraPrompt ?? "").Trim();
        var extraLead = JoyCaptionExtraPromptEngineering.BuildJoyExtraUserEnforcementTail(caption);
        var user = RenderTemplate(userTpl, new Dictionary<string, string>
        {
            ["task_lead"] = taskLead,
            ["length_block"] = CaptionLength.BuildCaptionLengthHint(caption.Len, caption.CaptionLenChars, caption.CaptionLang),
            ["extra_block"] = ep.Length > 0
                ? BlockOrEmpty(extraLead + (zh ? $"【附加要求】{ep}" : $"[Additional requirements] {ep}"))
                : "",
        });

        return new ReverseResolvedPrompts
        {
            CaptionType = capType,
            Builtin = false,
            System = system,
            UserLead = "",
            UserBody = user,
            OutputConstraints = zh
                ? " 【输出契约】只输出提示词正文，禁止 Markdown、思维链与自检标题。"
                : " [CONTRACT] Prompt text only; no markdown or chain-of-thought.",
            UserTail = "",
        };
    }

    // ==================== taxonomy（promptEngineeringTaxonomy.js 反推相关） ====================

    public static readonly IReadOnlyList<string> OutputFormats = new List<string>
    {
        "prose", "sd_tags", "danbooru_tags", "structured_md", "structured_json", "minimax",
    };

    /// <summary>输出格式目录（promptEngineeringTaxonomy.js OUTPUT_FORMATS）：id / 中文标签 / 可用分类。</summary>
    private static readonly (string Id, string Label, string[] Kinds)[] OutputFormatCatalog = new[]
    {
        ("prose", "自然语言", new[] { "expand", "reverse", "train" }),
        ("sd_tags", "SD 标签列表", new[] { "expand", "reverse", "train" }),
        ("danbooru_tags", "Danbooru 标签", new[] { "expand", "reverse", "train" }),
        ("structured_md", "结构化 Markdown", new[] { "expand", "reverse" }),
        ("structured_json", "结构化 JSON", new[] { "expand", "reverse" }),
        ("minimax", "MiniMax H3", new[] { "expand" }),
    };

    /// <summary>输出格式选项（getOutputFormatOptions(kind)）：按分类过滤，minimax 仅 expand。</summary>
    public static IReadOnlyList<(string Id, string Label)> GetOutputFormatOptions(string? kind)
        => OutputFormatCatalog
            .Where(f => string.IsNullOrEmpty(kind) || f.Kinds.Contains(kind))
            .Select(f => (f.Id, LocalizeFormatLabel(f.Id, f.Label)))
            .ToList();

    /// <summary>输出格式中文标签（getOutputFormatLabel(id)），未知回退原 id。</summary>
    public static string GetOutputFormatLabel(string? id)
        => LocalizeFormatLabel(id ?? "", OutputFormatCatalog.FirstOrDefault(f => f.Id == id).Label ?? id ?? "");

    /// <summary>输出格式标签本地化（zh/en 跟随应用语言；键缺失回退原始标签）。</summary>
    private static string LocalizeFormatLabel(string id, string fallback) => id switch
    {
        "prose" => Localizer.Instance?["PeFormatProse"] ?? fallback,
        "sd_tags" => Localizer.Instance?["PeFormatSdTags"] ?? fallback,
        "danbooru_tags" => Localizer.Instance?["PeFormatDanbooru"] ?? fallback,
        "structured_md" => Localizer.Instance?["PeFormatStructuredMd"] ?? fallback,
        "structured_json" => Localizer.Instance?["PeFormatStructuredJson"] ?? fallback,
        "minimax" => Localizer.Instance?["PeFormatMinimax"] ?? fallback,
        _ => fallback,
    };

    /// <summary>默认用户提示词模板（getDefaultUserTemplate(kind)）：反推/训练打标走反推模板，扩写走扩写模板。</summary>
    public string GetDefaultUserTemplate(string? kind)
        => kind == "reverse" || kind == "train" ? DefaultReverseUserTemplate : DefaultExpandUserTemplate;

    private static readonly Dictionary<string, string> ReverseOutputFormatByKey = new()
    {
        ["Descriptive"] = "prose",
        ["Descriptive_Casual"] = "prose",
        ["Straightforward"] = "prose",
        ["Stable_Diffusion_Prompt"] = "sd_tags",
        ["MidJourney"] = "sd_tags",
        ["Danbooru_tag_list"] = "danbooru_tags",
        ["e621_tag_list"] = "danbooru_tags",
        ["Rule34_tag_list"] = "danbooru_tags",
        ["Booru_tag_list"] = "danbooru_tags",
        ["Art_Critic"] = "prose",
        ["Product_Listing"] = "prose",
        ["Social_Media_Post"] = "prose",
    };

    private static readonly Dictionary<string, string> ToriiOutputFormatByCtype = new()
    {
        ["long_thoughts_v2"] = "structured_md",
        ["long_thoughts"] = "structured_md",
        ["min_structured_md"] = "structured_md",
        ["min_structured_md_body"] = "structured_md",
        ["md_comic"] = "structured_md",
        ["json"] = "structured_json",
        ["min_structured_json"] = "structured_json",
        ["json_comic"] = "structured_json",
        ["long"] = "prose",
        ["short"] = "prose",
    };

    public static string InferOutputFormat(PromptEngineeringProfile profile)
    {
        var explicitFmt = NormalizeString(profile.OutputFormat);
        if (OutputFormats.Contains(explicitFmt))
            return explicitFmt;
        if (profile.StructuredFormat || profile.ToriiFormat || profile.Id.StartsWith("pe_torii_", StringComparison.Ordinal) || profile.Id.StartsWith("pe_train_torii_", StringComparison.Ordinal))
        {
            var key = NormalizeString(profile.BuiltinKey);
            if (key.Length == 0)
                key = System.Text.RegularExpressions.Regex.Replace(profile.Id, @"^pe_(train_)?torii_", "");
            return ToriiOutputFormatByCtype.TryGetValue(key, out var f) ? f : "structured_md";
        }
        var bk = NormalizeString(profile.BuiltinKey);
        if (profile.Kind == "reverse" || profile.Kind == "train")
            return ReverseOutputFormatByKey.TryGetValue(bk, out var rf) ? rf : "prose";
        return "prose";
    }

    public static List<string> InferTags(PromptEngineeringProfile profile)
    {
        if (profile.Tags != null && profile.Tags.Count > 0)
            return profile.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (profile.StructuredFormat || profile.ToriiFormat || profile.Id.StartsWith("pe_torii_", StringComparison.Ordinal) || profile.Id.StartsWith("pe_train_torii_", StringComparison.Ordinal))
        {
            var key = NormalizeString(profile.BuiltinKey);
            if (key.Length == 0)
                key = System.Text.RegularExpressions.Regex.Replace(profile.Id, @"^pe_(train_)?torii_", "");
            var fmt = ToriiOutputFormatByCtype.TryGetValue(key, out var f) ? f : "structured_md";
            return fmt == "structured_json"
                ? new List<string> { Localizer.Instance?["PeSvcTagStructured"] ?? "", Localizer.Instance?["PeSvcTagJson"] ?? "" }
                : fmt == "prose"
                    ? new List<string> { Localizer.Instance?["PeSvcTagStructured"] ?? "", Localizer.Instance?["PeSvcTagNatural"] ?? "" }
                    : new List<string> { Localizer.Instance?["PeSvcTagStructured"] ?? "", Localizer.Instance?["PeSvcTagMarkdown"] ?? "" };
        }
        return new List<string> { Localizer.Instance?["PeSvcTagReverse"] ?? "" };
    }

    public static List<string> InferredSubjectDomains(PromptEngineeringProfile profile)
    {
        if (profile.SubjectDomains != null && profile.SubjectDomains.Count > 0)
            return profile.SubjectDomains;
        return new List<string> { "general" };
    }

    public static PromptEngineeringProfile EnrichProfileTaxonomy(PromptEngineeringProfile profile)
    {
        profile.OutputFormat = InferOutputFormat(profile);
        profile.Tags = InferTags(profile);
        profile.SubjectDomains = InferredSubjectDomains(profile);
        return profile;
    }

    private static string ApplyOutputLanguage(string system, string outputLang)
    {
        if ((outputLang ?? "zh") == "zh")
        {
            if (!system.Contains("简体中文") && !system.Contains("中文"))
                return system + "\n\n【输出语言锁定：中文】（最高优先级）你必须仅用中文输出反推结果，禁止英文标签串（专有名词除外）。";
            return system;
        }
        if ((outputLang ?? "").ToLowerInvariant() == "en")
        {
            return system + "\n\n[Output language lock: English] Write the caption in English only.";
        }
        return system;
    }
}
