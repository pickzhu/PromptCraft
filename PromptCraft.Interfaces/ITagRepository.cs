using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>标签仓储：查询、获取或创建、级联删除。</summary>
public interface ITagRepository
{
    Task<List<Tag>> GetAllAsync();
    Task<Tag?> GetByNameAsync(string name);
    Task<Tag> GetOrCreateAsync(string name, string category = "general");
    Task DeleteAsync(int tagId);
}
