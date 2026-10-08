using System.Text.Json.Serialization;

namespace PromptCraft.Service.PromptPlaza;

/// <summary>广场 list/mySubmits 响应（对齐 promptPlaza/list：{success, message, code, result:{datas,total,pageSize,pageNum}}）。</summary>
internal sealed class PlazaListResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    [JsonPropertyName("result")]
    public PlazaListResult? Result { get; set; }
}

/// <summary>广场列表分页结果。</summary>
internal sealed class PlazaListResult
{
    [JsonPropertyName("total")]
    public int Total { get; set; }
    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
    [JsonPropertyName("pageNum")]
    public int PageNum { get; set; }
    [JsonPropertyName("datas")]
    public List<PlazaItemDto>? Datas { get; set; }
}

/// <summary>广场 details 响应（对齐 promptPlaza/details：{success, message, code, result:{...}}）。</summary>
internal sealed class PlazaDetailResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    [JsonPropertyName("result")]
    public PlazaItemDto? Result { get; set; }
}

/// <summary>广场条目 DTO（list 与 details 共用字段；isRecommended 服务端为 0/1 数字）。</summary>
internal sealed class PlazaItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("models")]
    public string? Models { get; set; }
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }
    [JsonPropertyName("frontCover")]
    public string? FrontCover { get; set; }
    [JsonPropertyName("isRecommended")]
    public int IsRecommended { get; set; }
    [JsonPropertyName("viewCount")]
    public int ViewCount { get; set; }
    [JsonPropertyName("copyCount")]
    public int CopyCount { get; set; }
    [JsonPropertyName("seed")]
    public string? Seed { get; set; }
    [JsonPropertyName("positive")]
    public string? Positive { get; set; }
    [JsonPropertyName("negative")]
    public string? Negative { get; set; }
    [JsonPropertyName("sourcePromptId")]
    public string? SourcePromptId { get; set; }
    [JsonPropertyName("auditNote")]
    public string? AuditNote { get; set; }
    [JsonPropertyName("status")]
    public int Status { get; set; }
    [JsonPropertyName("createTimeStr")]
    public string? CreateTimeStr { get; set; }
    [JsonPropertyName("updateTimeStr")]
    public string? UpdateTimeStr { get; set; }
    [JsonPropertyName("authorUid")]
    public string? AuthorUid { get; set; }
    [JsonPropertyName("author")]
    public PlazaAuthorDto? Author { get; set; }
}

/// <summary>广场作者信息。</summary>
internal sealed class PlazaAuthorDto
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }
    [JsonPropertyName("headImgUrl")]
    public string? HeadImgUrl { get; set; }
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

/// <summary>广场 list 请求体（对齐 PromptMaster：平铺字段，无 data 包装；
/// models 为空时忽略，keyword 空串保留，等价 curl {"keyword":"","pageNumber":1,"pageSize":50,"sortType":"latest"}）。</summary>
internal sealed class PlazaListRequest
{
    [JsonPropertyName("keyword")]
    public string? Keyword { get; set; }
    [JsonPropertyName("models")]
    public string? Models { get; set; }
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }
    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
    [JsonPropertyName("sortType")]
    public string? SortType { get; set; }
}

/// <summary>我的投稿请求体（对齐 PromptMaster：平铺字段，无 data 包装）。</summary>
internal sealed class PlazaMySubmitsRequest
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }
    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
}
