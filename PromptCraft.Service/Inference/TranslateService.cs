using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.Inference;
using Ke.Bee.Localization.Localizer;
using Microsoft.Extensions.DependencyInjection;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 翻译服务（T4.1）。OpenAI 兼容引擎替代 HY-MT 本地模型。
/// 系统提示原文：资产提取 §5.2（zh2en / en2zh 两句），temperature 0.2。
/// 语言判定：CJK 字符占比 &gt; 12% → 视为中文源。
/// </summary>
public sealed class TranslateService : ITranslateService
{
    private const string SystemZh2En =
        "Translate the following Chinese AI image prompt to English. Preserve every weight like (word:1.2), line breaks, and tag commas. Output only the translation, no quotes.";
    private const string SystemEn2Zh =
        "Translate the following English AI image prompt to Chinese. Preserve every weight like (word:1.2), line breaks, and tag commas. Output only the translation, no quotes.";

    private readonly IServiceProvider _services;
    public TranslateService(IServiceProvider services) => _services = services;

    public async Task<string> TranslateAsync(string text, TranslateDirection dir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";

        // T0.4：根据全局 TranslateEngine 选择百度 / OpenAI。
        var cfg = ConfigRepository.LoadFromDb();
        if (cfg != null && string.Equals(cfg.TranslateEngine, "baidu", StringComparison.OrdinalIgnoreCase))
        {
            var isZhSource = dir switch
            {
                TranslateDirection.Zh2En => true,
                TranslateDirection.En2Zh => false,
                _ => IsCjkDominant(text),
            };
            return await BaiduTranslateSigner.TranslateAsync(
                cfg.BaiduAppId, cfg.BaiduAppKey, text,
                from: isZhSource ? "zh" : "en",
                to: isZhSource ? "en" : "zh",
                ct: ct);
        }

        var providerSvc = _services.GetRequiredService<IProviderService>();
        var pick = await providerSvc.GetDefaultExpandAsync(ct); // 翻译复用文本模型
        if (pick == null)
        {
            throw new InferencesException(InferenceErrorKind.NoSuchProvider,
                Localizer.Instance?["NoTextModelConfigured"] ?? "");
        }
        var (provider, model) = pick.Value;

        var isZhSource2 = dir switch
        {
            TranslateDirection.Zh2En => true,
            TranslateDirection.En2Zh => false,
            _ => IsCjkDominant(text),
        };
        var system = isZhSource2 ? SystemZh2En : SystemEn2Zh;

        var body = new ChatCompletionRequest
        {
            Model = model.ModelName,
            Temperature = 0.2,
            MaxTokens = 1024,
            Messages =
            {
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = text },
            },
        };
        ThinkingModelHeuristic.ApplyThinkingOff(body);

        var resp = await OpenAiHttpHelper.ChatAsync(provider.BaseUrl, provider.ApiKey, body, 180, ct);
        var output = resp.Choices.Count > 0 ? resp.Choices[0].Message?.Content as string ?? "" : "";
        return ExpandService.StripThinkingTags(output);
    }

    /// <summary>资产提取 §5.1：CJK 占比 &gt; 12% → 中文。</summary>
    internal static bool IsCjkDominant(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var cjk = text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
        return (double)cjk / text.Length > 0.12;
    }
}
