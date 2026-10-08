using PromptCraft.Models.Inference;
using Ke.Bee.Localization.Localizer;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 百度翻译签名链路（T4.1，原文迁移自 app/electron/utils/TranslateUtils.js）。
/// sign = md5(appid + q + salt + key)；GET fanyi-api.baidu.com/api/trans/vip/translate。
/// </summary>
public static class BaiduTranslateSigner
{
    private static readonly HttpClient Shared = new();

    public static string Sign(string appId, string text, long salt, string appKey)
    {
        var raw = appId + text + salt + appKey;
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static async Task<string> TranslateAsync(string appId, string appKey, string text,
        string from = "en", string to = "zh", CancellationToken ct = default)
    {
        var salt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sign = Sign(appId, text, salt, appKey);
        var url = $"https://fanyi-api.baidu.com/api/trans/vip/translate" +
                  $"?q={Uri.EscapeDataString(text)}&from={from}&to={to}" +
                  $"&appid={appId}&salt={salt}&sign={sign}";

        using var resp = await Shared.GetAsync(url, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("trans_result", out var arr) && arr.ValueKind == JsonValueKind.Array
            && arr.GetArrayLength() > 0)
        {
            return arr[0].GetProperty("dst").GetString() ?? "";
        }
        var err = doc.RootElement.TryGetProperty("error_msg", out var e) ? e.GetString() : json;
        throw new InferencesException(InferenceErrorKind.NetworkError, string.Format(Localizer.Instance?["BaiduTranslateFailed"] ?? "", err));
    }
}
