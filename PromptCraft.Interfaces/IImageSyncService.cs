using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>
/// 图片同步服务：扫描 ComfyUI 输出目录，将图片连同元数据/缩略图同步入库，删除图片。
/// </summary>
public interface IImageSyncService
{
    /// <summary>扫描输出目录并同步图片到数据库。</summary>
    Task<SyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>为指定图片生成缩略图文件。</summary>
    Task<string> GenerateThumbnailAsync(string sourcePath, string relativePath, CancellationToken ct);

    /// <summary>按图片 id 删除图片（连带缩略图与记录）。</summary>
    Task<ImageDeleteResult> DeleteImagesAsync(IReadOnlyCollection<int> imageIds, CancellationToken ct = default);
}
