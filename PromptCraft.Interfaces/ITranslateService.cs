namespace PromptCraft.Interfaces;

/// <summary>翻译方向：auto=按 12% CJK 判定；zh2en / en2zh 显式指定。</summary>
public enum TranslateDirection { Auto, Zh2En, En2Zh }

/// <summary>翻译服务（T4.1）。OpenAI 兼容引擎替代 HY-MT 本地模型，或百度翻译（TranslateEngine=baidu）。</summary>
public interface ITranslateService
{
    Task<string> TranslateAsync(string text, TranslateDirection dir, CancellationToken ct);
}
