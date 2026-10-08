using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>图片元数据仓储：查询、增删。</summary>
public interface IImageMetadataRepository
{
    Task<List<ImageInfo>> GetAllAsync();
    Task<ImageInfo?> GetByHashAsync(string hash);
    Task AddAsync(ImageInfo img);
    Task AddRangeAsync(IEnumerable<ImageInfo> items);
    Task DeleteAsync(ImageInfo img);
}
