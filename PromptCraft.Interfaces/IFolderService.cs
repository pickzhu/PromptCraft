using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>统一文件夹 Scope 常量（单级文件夹，按资产类型各自独立）。</summary>
public static class FolderScopes
{
    /// <summary>任意类型（新建文件夹不再选类型：文件夹可放工作流/提示词/图库任意资产）。</summary>
    public const string All = "All";
    /// <summary>工作流资产。</summary>
    public const string Workflow = "Workflow";
    /// <summary>提示词资产。</summary>
    public const string Prompt = "Prompt";
    /// <summary>图库图片资产。</summary>
    public const string Gallery = "Gallery";
}

/// <summary>
/// 统一单级文件夹服务（工作流 / 提示词 / 图库，用 <see cref="Folder.Scope"/> 区分）。
/// 只支持一级：无父子层级；资产与文件夹为多对多（PromptFolderMaps / WorkflowFolderMaps / GalleryFolderMaps）。
/// </summary>
public interface IFolderService
{
    /// <summary>取某 Scope 的全部文件夹（按 sort 升序）。</summary>
    Task<List<Folder>> GetFoldersAsync(string scope, CancellationToken ct = default);

    /// <summary>取全部文件夹（忽略 Scope：文件夹可放任意类型资产，按 sort 升序）。</summary>
    Task<List<Folder>> GetAllFoldersAsync(CancellationToken ct = default);

    /// <summary>保存文件夹（新建或重命名）。同 Scope 内同名拒绝（忽略大小写）。</summary>
    Task<Folder> SaveFolderAsync(Folder folder, CancellationToken ct = default);

    /// <summary>
    /// 删除文件夹。deleteAssets=false（默认）：仅移除文件夹与资产的关联（资产保留，变为未分类）；
    /// deleteAssets=true：连同其中资产一起删除（提示词物理删、工作流逻辑删、图库物理删+删文件），
    /// 并清理这些资产在其他文件夹的关联。
    /// </summary>
    Task DeleteFolderAsync(int id, bool deleteAssets, CancellationToken ct = default);

    /// <summary>按给定 id 顺序重排某 Scope 文件夹（sort 升序重写）。</summary>
    Task<List<Folder>> ReorderFoldersAsync(string scope, IReadOnlyList<int> orderedIds, CancellationToken ct = default);

    /// <summary>
    /// 把资产加入若干文件夹（多对多关联）。assetIds 为资产 Id 字符串：
    /// Prompt 用主键字符串；Workflow/Gallery 用 int 的字符串形式。仅关联真实存在的文件夹与资产，重复关联自动跳过。
    /// </summary>
    Task AddToFoldersAsync(string scope, IReadOnlyList<string> assetIds, IReadOnlyList<int> folderIds, CancellationToken ct = default);

    /// <summary>
    /// 从文件夹移除资产（多对多；仅删关联，不删资产）。folderIds=null 表示从该资产所属的所有文件夹移除。
    /// </summary>
    Task RemoveFromFoldersAsync(string scope, IReadOnlyList<string> assetIds, IReadOnlyList<int>? folderIds, CancellationToken ct = default);

    /// <summary>某资产当前所属文件夹 Id 集合。</summary>
    Task<HashSet<int>> GetAssetFolderIdsAsync(string scope, string assetId, CancellationToken ct = default);
}
