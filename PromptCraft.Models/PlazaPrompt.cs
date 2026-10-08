using System.Collections.Generic;

namespace PromptCraft.Models;

/// <summary>
/// 提示词广场条目（对齐 PromptMaster PromptPlaza list/details 接口）。
/// list 接口只返回基础字段（title/cover/models/viewCount/copyCount/author），
/// 正向/反向提示词在 details 接口中按需获取。
/// </summary>
public sealed class PlazaPrompt
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Models { get; set; } = "";
    /// <summary>逗号/中文逗号分隔的原始标签串（由 UI 拆分展示）。</summary>
    public string Tags { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Positive { get; set; } = "";
    public string Negative { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public bool IsRecommended { get; set; }
    public int ViewCount { get; set; }
    public int CopyCount { get; set; }
    /// <summary>作者昵称（回退 authorUid / "匿名"，对齐 PromptMaster Na()）。</summary>
    public string AuthorName { get; set; } = "";

    /// <summary>作者头像 URL（author.headImgUrl，可为空；UI 加载失败显示首字占位）。</summary>
    public string AuthorAvatarUrl { get; set; } = "";
}

/// <summary>
/// 广场分页结果（对齐 promptPlaza/list 响应 result：datas 列表 + total 总条数）。
/// Total 用于 UI 展示"共 N 条"（服务端总量），Items 为本页数据。
/// </summary>
public sealed class PlazaPageResult
{
    public IReadOnlyList<PlazaPrompt> Items { get; set; } = Array.Empty<PlazaPrompt>();
    public int Total { get; set; }
}
