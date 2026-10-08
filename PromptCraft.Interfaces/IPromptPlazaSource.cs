using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PromptCraft.Models;

namespace PromptCraft.Interfaces;

/// <summary>
/// 提示词广场数据源（对齐 PromptMaster promptPlaza API：https://api.comfyit.cn/）。
/// 提供列表/我的投稿/详情/复制上报四个云端接口；接口不可达或鉴权失败时抛异常，由 UI 展示错误空态。
/// </summary>
public interface IPromptPlazaSource
{
    /// <summary>是否已配置云后端（本实现恒为 true；保留扩展位）。</summary>
    bool IsConfigured { get; }

    /// <summary>拉取广场列表（promptPlaza/list；keyword/models/sortType 筛选，latest/view/copy 排序；
    /// pageNumber/pageSize 分页，Total 为服务端总条数，用于滚动加载 HasMore 判断）。</summary>
    Task<PlazaPageResult> FetchAsync(string? keyword, string? models, string sortType, int pageNumber, int pageSize, CancellationToken ct);

    /// <summary>拉取我的投稿（promptPlaza/mySubmits；无账号体系时服务端可能返回错误，由 UI 展示）。</summary>
    Task<PlazaPageResult> FetchMySubmitsAsync(int pageNumber, int pageSize, CancellationToken ct);

    /// <summary>拉取单条详情（promptPlaza/details，含正向/反向提示词）。</summary>
    Task<PlazaPrompt> FetchDetailAsync(string id, CancellationToken ct);

    /// <summary>上报一次复制（promptPlaza/reportCopy，失败静默）。</summary>
    Task ReportCopyAsync(string id, CancellationToken ct);
}
