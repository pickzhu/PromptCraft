using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>执行输出仓储：批量保存。</summary>
public interface IJobOutputRepository
{
    Task AddRangeAsync(IEnumerable<JobOutput> outputs);
}
