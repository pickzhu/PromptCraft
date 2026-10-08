using System.ComponentModel.DataAnnotations.Schema;
using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Models;

/// <summary>
/// 提示词库实体（已并入 comfyui.db 单库，表/列一律大驼峰命名）。
/// 文件夹改用统一 <see cref="Folder"/>（Scope="Prompt"）；标签使用全局标签池（Tag + PromptTagMap）。
/// </summary>
public class Prompt
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Positive { get; set; } = "";
    public string Negative { get; set; } = "";
    /// <summary>封面相对路径（工作空间 covers\ 内）。</summary>
    public string Cover { get; set; } = "";
    public string Note { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Models { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>该 prompt 关联的标签映射（EF 导航；标签实体经 PromptTagMap 多对多，TagId 存全局 Tag.Id 的字符串）。</summary>
    public ICollection<PromptTagMap> PromptTags { get; set; } = new List<PromptTagMap>();

    /// <summary>所属文件夹关联（多对多；空集合=未分类，一个提示词可同时属于多个文件夹）。</summary>
    public ICollection<PromptFolderMap> FolderMaps { get; set; } = new List<PromptFolderMap>();

    /// <summary>保存时传入的文件夹 Id 集合（非映射属性；服务据此重建 FolderMaps）。</summary>
    [NotMapped]
    public IReadOnlyList<int> FolderIds { get; set; } = Array.Empty<int>();
}

/// <summary>PromptTagMaps 联合主键表（PromptId, TagId；TagId 为全局 Tag.Id 的字符串形式）。</summary>
public class PromptTagMap
{
    public string PromptId { get; set; } = "";
    public string TagId { get; set; } = "";
}

/// <summary>Prompt-Folder 多对多关联表（一个提示词可同时属于多个文件夹；删除文件夹/删除提示词均级联清理关联）。</summary>
public class PromptFolderMap
{
    public string PromptId { get; set; } = "";
    [ForeignKey(nameof(PromptId))] public Prompt? Prompt { get; set; }
    public int FolderId { get; set; }
    [ForeignKey(nameof(FolderId))] public Folder? Folder { get; set; }
}
