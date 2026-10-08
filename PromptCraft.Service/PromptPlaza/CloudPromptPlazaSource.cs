using PromptCraft.Interfaces;
using PromptCraft.Models;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptCraft.Service.PromptPlaza;

/// <summary>
/// 提示词广场云端数据源（对齐 PromptMaster promptPlaza API）。
/// 接口：POST promptPlaza/list、GET promptPlaza/details、GET promptPlaza/reportCopy，基地址 https://api.comfyit.cn/。
/// 响应一律用强类型 DTO 反序列化（System.Net.Http.Json），请求头对齐 prompt_master 客户端
/// （platform=prompt_master；token/userId/mac/version 本端无账号体系，留空由服务端按游客处理）。
/// </summary>
public sealed class CloudPromptPlazaSource : IPromptPlazaSource
{
    private const string BaseUrl = "https://api.comfyit.cn/";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _http;
    private readonly IBaseLogService _log;

    public CloudPromptPlazaSource(IBaseLogService? log = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("platform", "prompt_master");
        _log = log ?? LogService.Instance;
    }

    public bool IsConfigured => true;

    public async Task<PlazaPageResult> FetchAsync(string? keyword, string? models, string sortType, int pageNumber, int pageSize, CancellationToken ct)
    {
        var request = new PlazaListRequest
        {
            Keyword = keyword?.Trim() ?? "",
            Models = string.IsNullOrWhiteSpace(models) ? null : models.Trim(),
            PageNumber = pageNumber,
            PageSize = pageSize,
            SortType = string.IsNullOrWhiteSpace(sortType) ? "latest" : sortType.Trim(),
        };
        var resp = await PostListAsync<PlazaListResponse>("promptPlaza/list", request, ct);
        return MapList(resp, "提示词广场");
    }

    public async Task<PlazaPageResult> FetchMySubmitsAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        var request = new PlazaMySubmitsRequest { PageNumber = pageNumber, PageSize = pageSize };
        var resp = await PostListAsync<PlazaListResponse>("promptPlaza/mySubmits", request, ct);
        return MapList(resp, "我的投稿");
    }

    public async Task<PlazaPrompt> FetchDetailAsync(string id, CancellationToken ct)
    {
        var resp = await _http.GetAsync(BaseUrl + "promptPlaza/details?id=" + Uri.EscapeDataString(id), ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<PlazaDetailResponse>(JsonOpts, ct);
        if (dto == null || !dto.Success)
            throw new InvalidOperationException(dto?.Message is { Length: > 0 } msg ? msg : "请求失败");
        if (dto.Result == null)
            throw new InvalidOperationException("详情数据为空");
        return MapItem(dto.Result);
    }

    public async Task ReportCopyAsync(string id, CancellationToken ct)
    {
        try
        {
            await _http.GetAsync(BaseUrl + "promptPlaza/reportCopy?id=" + Uri.EscapeDataString(id), ct);
        }
        catch (Exception ex)
        {
            _log.Warn($"提示词广场复制上报失败: {ex.Message}", "PromptPlaza", ex);
        }
    }

    // ==================== 内部 ====================

    private async Task<T?> PostListAsync<T>(string path, object payload, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsJsonAsync(BaseUrl + path, payload, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>解析 list/mySubmits 响应（{success, result:{datas,total}}），Total 为服务端总条数。</summary>
    private PlazaPageResult MapList(PlazaListResponse? resp, string tag)
    {
        if (resp == null || !resp.Success)
            throw new InvalidOperationException(resp?.Message is { Length: > 0 } msg ? msg : "请求失败");
        var list = new List<PlazaPrompt>();
        if (resp.Result?.Datas is { Count: > 0 } datas)
        {
            foreach (var d in datas)
                list.Add(MapItem(d));
        }
        _log.Info($"{tag}拉取 {list.Count} 条，共 {resp.Result?.Total ?? 0} 条", "PromptPlaza");
        return new PlazaPageResult { Items = list, Total = resp.Result?.Total ?? 0 };
    }

    /// <summary>条目映射（list 与 details 共用字段映射到业务模型）。</summary>
    private static PlazaPrompt MapItem(PlazaItemDto d)
    {
        return new PlazaPrompt
        {
            Id = d.Id.ToString(),
            Title = d.Title ?? "",
            Description = d.Description ?? "",
            Models = d.Models ?? "",
            Tags = d.Tags ?? "",
            CoverUrl = d.FrontCover ?? "",
            IsRecommended = d.IsRecommended == 1,
            ViewCount = d.ViewCount,
            CopyCount = d.CopyCount,
            Seed = d.Seed ?? "",
            Positive = d.Positive ?? "",
            Negative = d.Negative ?? "",
            AuthorName = AuthorName(d),
            AuthorAvatarUrl = d.Author?.HeadImgUrl?.Trim() ?? "",
        };
    }

    /// <summary>作者名解析（对齐 PromptMaster Na()：author.nickname→author.uid→authorUid→匿名）。</summary>
    private static string AuthorName(PlazaItemDto d)
    {
        if (d.Author != null)
        {
            var nick = d.Author.Nickname?.Trim();
            if (nick.Length > 0) return nick;
            var uid = d.Author.Uid?.Trim();
            if (uid.Length > 0) return uid;
        }
        var authorUid = d.AuthorUid?.Trim();
        return authorUid.Length > 0 ? authorUid : "匿名";
    }
}
