using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using Ke.Bee.Localization.Localizer;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 提示词仿写服务（单次生成）。复用扩写默认模型（GetDefaultExpandAsync），
/// temperature 0.7 / top_p 0.9 / max_tokens 4096，输出前走 StripThinkingTags 清洗。
/// 识别到「改成男性/女性」时：① 注入性别专项改写块；② 生成后做性别守卫，
/// 若结果仍残留对侧性别特征措辞，自动触发一次校正重写（强制执行，不依赖模型自觉）。
/// </summary>
public sealed class ImitationService : IImitationService
{
    private readonly IProviderService _providerSvc;
    private const int DefaultMaxTokens = 4096;
    private const int GuardMaxTokens = 2048;

    // 女性特征措辞（目标为男性时需清除）
    private static readonly string[] FeminineMarkers =
    {
        "高髻", "步摇", "珠翠", "流苏", "发饰", "簪花", "钿花",
        "薄纱", "半透纱", "长裙", "拖尾", "裙裾", "襦裙", "纱裙",
        "温婉", "清冷", "鹅蛋脸", "杏眼", "柔美", "婀娜",
    };
    // 男性特征措辞（目标为女性时需清除）
    private static readonly string[] MasculineMarkers =
    {
        "束发", "高马尾", "幞头", "发冠", "圆领袍", "长衫", "剑眉", "星目",
        "英朗", "挺拔", "英气", "胡须",
    };

    public ImitationService(IServiceProvider services)
    {
        _providerSvc = services.GetRequiredService<IProviderService>();
    }

    public Task<string> ImitateAsync(ImitationRequest req, CancellationToken ct)
        => ImitateAsync(req, null, null, ct);

    public async Task<string> ImitateAsync(ImitationRequest req, ProviderConfig? provider, ProviderModel? model, CancellationToken ct)
    {
        var source = (req.SourcePrompt ?? "").Trim();
        var requirement = (req.Requirement ?? "").Trim();
        if (source.Length == 0)
        {
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["ImitationSourceRequired"] ?? "请先输入原始提示词");
        }
        if (requirement.Length == 0)
        {
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["ImitationRequirementRequired"] ?? "请先输入仿写要求");
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

        var outputLang = string.IsNullOrEmpty(req.OutputLang) ? "zh" : req.OutputLang;
        var targetGender = DetectTargetGender(requirement);
        var system = ImitationPromptEngineering.BuildSystem(requirement, req.FormatId, outputLang, targetGender);
        var user = ImitationPromptEngineering.BuildUser(source, outputLang);

        var body = new ChatCompletionRequest
        {
            Model = model.ModelName,
            Temperature = 0.7,
            TopP = 0.9,
            MaxTokens = DefaultMaxTokens,
            Messages =
            {
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = user },
            },
        };
        ThinkingModelHeuristic.ApplyThinkingOff(body);

        var resp = await OpenAiHttpHelper.ChatAsync(provider.BaseUrl, provider.ApiKey, body, 180000, ct);
        if (resp.Choices.Count == 0)
        {
            throw new InferencesException(InferenceErrorKind.NetworkError,
                string.Format(Localizer.Instance?["ExpandEmptyResponseFormat"] ?? "{0}：返回为空", provider.Name ?? ""));
        }
        var content = resp.Choices[0].Message?.Content?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InferencesException(InferenceErrorKind.NetworkError,
                string.Format(Localizer.Instance?["ExpandEmptyResponseFormat"] ?? "{0}：返回为空", provider.Name ?? ""));
        }
        content = ExpandService.StripThinkingTags(content);

        // 性别守卫：目标为男/女且结果仍残留对侧性别措辞 → 校正一次
        if (targetGender != null)
        {
            content = await EnsureGenderConsistencyAsync(content, targetGender, provider, model, outputLang, ct);
        }
        return content;
    }

    /// <summary>从仿写要求中识别目标性别：male / female / null（未点名）。</summary>
    internal static string? DetectTargetGender(string requirement)
    {
        var r = requirement ?? "";
        // 优先精确词
        var male = r.Contains("男性") || r.Contains("男生") || r.Contains("少年") || r.Contains("男子")
                   || r.Contains("改成男") || r.Contains("换成男") || r.Contains("变成男")
                   || r.Contains("male", StringComparison.OrdinalIgnoreCase)
                   || r.Contains("boy", StringComparison.OrdinalIgnoreCase);
        var female = r.Contains("女性") || r.Contains("女生") || r.Contains("少女") || r.Contains("女子")
                     || r.Contains("改成女") || r.Contains("换成女") || r.Contains("变成女")
                     || r.Contains("female", StringComparison.OrdinalIgnoreCase)
                     || r.Contains("girl", StringComparison.OrdinalIgnoreCase);
        if (male && !female) return "male";
        if (female && !male) return "female";
        return null; // 同时命中或均未命中 → 不启用性别守卫
    }

    /// <summary>生成后守卫：结果残留对侧性别措辞时，发起一次校正重写。</summary>
    private async Task<string> EnsureGenderConsistencyAsync(
        string content, string targetGender, ProviderConfig provider, ProviderModel model, string outputLang, CancellationToken ct)
    {
        var markers = targetGender == "male" ? FeminineMarkers : MasculineMarkers;
        if (!markers.Any(m => content.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return content;

        var corrected = await CorrectiveRewriteAsync(content, targetGender, provider, model, outputLang, ct);
        return corrected ?? content;
    }

    private async Task<string?> CorrectiveRewriteAsync(
        string content, string targetGender, ProviderConfig provider, ProviderModel model, string outputLang, CancellationToken ct)
    {
        try
        {
            var useEn = (outputLang ?? "zh") == "en";
            string system;
            string intro;
            if (targetGender == "male")
            {
                system = useEn
                    ? "You are a prompt gender-correction reviewer. The prompt below was supposed to be rewritten to MALE but still contains female-specific descriptions. Fix them: remove or replace every female hair/hairstyle ornament (high bun, step-sway hairpin, beaded/tassel ornaments, hairpins), female clothing (sheer, translucent gauze, long trailing skirt, skirt hem), female face/temperament (oval face, apricot eyes, gentle-cool, soft), and overly small build (height/weight) with male equivalents. Keep everything else unchanged. Output ONLY the corrected full prompt."
                    : "你是提示词性别校对员。下面这条提示词本应改成「男性」，但残留了女性特征描述。请修正：把仍属于女性的发型发饰（高髻、步摇、珠翠、流苏发饰、簪花）、服装（薄纱、半透纱、长裙拖尾、裙裾、襦裙）、面容气质（鹅蛋脸、杏眼、温婉清冷、柔美）以及过矮过轻的体格，全部删除或替换为男性对应（束发/幞头/发冠、圆领袍/长衫、剑眉星目、清俊英朗、身高体重正常男性体格）；其余内容保持不变。只输出修正后的完整提示词，不要解释。";
                intro = useEn
                    ? "Correct this MALE prompt (remove remaining female descriptions):\n\n"
                    : "请修正这条应为「男性」的提示词（删除残留的女性描述）：\n\n";
            }
            else
            {
                system = useEn
                    ? "You are a prompt gender-correction reviewer. The prompt below was supposed to be rewritten to FEMALE but still contains male-specific descriptions. Fix them: remove or replace every male hairstyle (top-knot, futou headwrap, hair crown, beard), male clothing (round-collar robe, long robe), male face/temperament (sharp brows, clear-handsome, defined jawline) and overly large build with female equivalents. Keep everything else unchanged. Output ONLY the corrected full prompt."
                    : "你是提示词性别校对员。下面这条提示词本应改成「女性」，但残留了男性特征描述。请修正：把仍属于男性的发型发饰（束发、高马尾、幞头、发冠、胡须）、服装（圆领袍、长衫、袍服）、面容气质（剑眉星目、清俊英朗、下颌硬朗、英气）以及过高的体格，全部删除或替换为女性对应（高髻、盘发、步摇珠翠、襦裙长裙薄纱、鹅蛋脸杏眼温婉清冷）；其余内容保持不变。只输出修正后的完整提示词，不要解释。";
                intro = useEn
                    ? "Correct this FEMALE prompt (remove remaining male descriptions):\n\n"
                    : "请修正这条应为「女性」的提示词（删除残留的男性描述）：\n\n";
            }

            var body = new ChatCompletionRequest
            {
                Model = model.ModelName,
                Temperature = 0.3,
                TopP = 0.8,
                MaxTokens = GuardMaxTokens,
                Messages =
                {
                    new ChatMessage { Role = "system", Content = system },
                    new ChatMessage { Role = "user", Content = intro + content },
                },
            };
            ThinkingModelHeuristic.ApplyThinkingOff(body);

            var resp = await OpenAiHttpHelper.ChatAsync(provider.BaseUrl, provider.ApiKey, body, 120000, ct);
            if (resp.Choices.Count == 0) return null;
            var outText = resp.Choices[0].Message?.Content?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(outText)) return null;
            return ExpandService.StripThinkingTags(outText);
        }
        catch
        {
            return null; // 校正失败则回退原结果
        }
    }
}
