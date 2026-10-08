using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>图片状态仓储：更新状态。</summary>
public interface IImageStatusRepository
{
    Task UpdateAsync(ImageStatus status);
}
