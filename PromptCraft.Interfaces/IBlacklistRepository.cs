namespace PromptCraft.Interfaces;

/// <summary>黑名单哈希仓储：判断图片是否在黑名单。</summary>
public interface IBlacklistRepository
{
    Task<bool> ExistsAsync(string hash);
}
