using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PromptCraft.Models.ComfyUI;

#region 图片元数据

public class ImageInfo
{
    [Key] public int Id { get; set; }
    [Required, MaxLength(512)] public string RelativePath { get; set; } = string.Empty;
    [Required, MaxLength(256)] public string FileName { get; set; } = string.Empty;
    [MaxLength(16)] public string Extension { get; set; } = ".png";
    public long FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string? Hash { get; set; }

    /// <summary>
    /// 关联的图片提取工作流（<see cref="Workflow"/>，按顶级 WorkflowGuid 去重复用）。
    /// 为 null 表示该图无 ComfyUI 工作流元数据。按工作流分类图片时用它分组。
    /// </summary>
    public int? WorkflowId { get; set; }
    [ForeignKey(nameof(WorkflowId))] public Workflow? Workflow { get; set; }

    /// <summary>
    /// 关联的提示词/生成参数（<see cref="ImagePrompt"/>，同一工作流内按 PromptHash 去重复用）。
    /// 为 null 表示该图无提示词元数据。
    /// </summary>
    public int? PromptId { get; set; }
    [ForeignKey(nameof(PromptId))] public ImagePrompt? Prompt { get; set; }

    public ImageStatus? Status { get; set; }
    public ICollection<ImageTag> ImageTags { get; set; } = new List<ImageTag>();

    /// <summary>所属文件夹关联（多对多；空集合=未分类，一张图片可同时属于多个文件夹）。</summary>
    public ICollection<GalleryFolderMap> FolderMaps { get; set; } = new List<GalleryFolderMap>();
}

/// <summary>
/// 工作流（统一表：用户工作流与从 ComfyUI 图片提取的工作流并存，用 <see cref="Source"/> 区分）。
/// 图片提取工作流：Source="image"，按顶级 WorkflowGuid（ComfyUI 工作流 UUID）唯一去重，
/// 结构版 JSON（参数已剔除）GZip 压缩存 <see cref="WorkflowJsonBlob"/>，
/// 详情/对比展示时用该图片的参数（提示词表 NodesJsonBlob）组装回完整工作流。
/// 用户工作流：Source="user"，完整 JSON GZip 压缩存 <see cref="WorkflowJsonBlob"/>，SourcePath 去重。
/// 统一读取入口：<see cref="ImageMetadataExtensions.GetWorkflowJson"/>（按需解压）。
/// </summary>
public class Workflow
{
    [Key] public int Id { get; set; }
    [Required, MaxLength(256)] public string Name { get; set; } = string.Empty;
    [MaxLength(512)] public string? ThumbnailPath { get; set; }

    /// <summary>ComfyUI 上的文件相对路径（如 workflows/xxx.json）。用户工作流去重键，唯一索引。</summary>
    [MaxLength(512)] public string? SourcePath { get; set; }

    /// <summary>来源："user"=用户工作流 / "image"=从图片提取。列表展示按来源区分。</summary>
    [Required, MaxLength(16)] public string Source { get; set; } = "user";

    /// <summary>ComfyUI 工作流顶级 UUID（图片提取工作流的唯一去重键；用户工作流可为空）。唯一索引。</summary>
    [MaxLength(64)] public string? WorkflowGuid { get; set; }

    /// <summary>工作流 JSON 的 GZip 压缩字节：用户工作流存完整 JSON（Source="user"），图片提取工作流存结构版（Source="image"）。</summary>
    public byte[]? WorkflowJsonBlob { get; set; }

    /// <summary>工作流节点数（图片提取时统计）</summary>
    public int NodeCount { get; set; }

    /// <summary>逻辑删除标记：列表/检索时过滤，图片详情等追溯场景仍可读取。</summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<WorkflowInput> Inputs { get; set; } = new List<WorkflowInput>();
    public ICollection<WorkflowJob> Jobs { get; set; } = new List<WorkflowJob>();

    /// <summary>所属文件夹关联（多对多；空集合=未分类，一个工作流可同时属于多个文件夹）。</summary>
    public ICollection<WorkflowFolderMap> FolderMaps { get; set; } = new List<WorkflowFolderMap>();

    /// <summary>该工作流关联的图片提取提示词（Source="image" 时使用）</summary>
    public ICollection<ImagePrompt> Prompts { get; set; } = new List<ImagePrompt>();

    /// <summary>关联的图片（按工作流分类时用它分组）</summary>
    public ICollection<ImageInfo> Images { get; set; } = new List<ImageInfo>();

    /// <summary>工作流标签关联（与图库共用 Tag 表，Tag 池统一）。</summary>
    public ICollection<WorkflowTag> WorkflowTags { get; set; } = new List<WorkflowTag>();
}

/// <summary>
/// 从 ComfyUI 图片提取的提示词与生成参数（单独表，关联 <see cref="Workflow"/>）。
/// 同一工作流内按 (WorkflowId, PromptHash) 唯一 → 同一工作流+同一提示词只存一条。
/// prompt JSON 以 GZip 压缩存 <see cref="PromptJsonBlob"/>，常用参数拆成列便于筛选/展示。
/// </summary>
public class ImagePrompt
{
    [Key] public int Id { get; set; }

    public int WorkflowId { get; set; }
    [ForeignKey(nameof(WorkflowId))] public Workflow Workflow { get; set; } = null!;

    /// <summary>SHA256(prompt 原始 JSON)。与 WorkflowId 联合唯一索引（去重键）。</summary>
    [Required, MaxLength(64)] public string PromptHash { get; set; } = string.Empty;

    /// <summary>GZip 压缩的 API 格式 prompt JSON（节点 id → {class_type, inputs}）。</summary>
    public byte[]? PromptJsonBlob { get; set; }

    /// <summary>提取的正提示词（明文，便于搜索/展示）</summary>
    [MaxLength(16384)] public string? PositivePrompt { get; set; }

    /// <summary>提取的负提示词</summary>
    [MaxLength(16384)] public string? NegativePrompt { get; set; }

    /// <summary>Checkpoint / UNET 模型名</summary>
    [MaxLength(512)] public string? Model { get; set; }

    [MaxLength(32)] public string? Seed { get; set; }
    [MaxLength(16)] public string? Steps { get; set; }
    [MaxLength(16)] public string? Cfg { get; set; }
    [MaxLength(64)] public string? Sampler { get; set; }
    [MaxLength(64)] public string? Scheduler { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>LoRA 模型名，逗号分隔</summary>
    [MaxLength(2048)] public string? LoraNames { get; set; }

    /// <summary>GZip 压缩的节点快照 JSON（ExtractedMetadata.Nodes，尽可能全的参数）</summary>
    public byte[]? NodesJsonBlob { get; set; }

    /// <summary>逻辑删除标记：图片详情仍可读取（工作流删除时随其逻辑删除）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ImageInfo> Images { get; set; } = new List<ImageInfo>();
}

public class ImageStatus
{
    [Key] public int Id { get; set; }
    public int ImageInfoId { get; set; }
    [ForeignKey(nameof(ImageInfoId))] public ImageInfo Image { get; set; } = null!;
    public bool IsDeleted { get; set; }
    public bool IsFavorite { get; set; }
    public int Rating { get; set; }
    [MaxLength(1024)] public string? Notes { get; set; }
    public bool IsNsfw { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Tag
{
    [Key] public int Id { get; set; }
    [Required, MaxLength(128)] public string Name { get; set; } = string.Empty;
    [MaxLength(32)] public string Category { get; set; } = "general";

    /// <summary>标签颜色（hex 字符串 "#RRGGBB"，创建标签时从 <see cref="TagPalette"/> 随机生成并持久化；老数据可为 null，展示时回退名称哈希色）。</summary>
    [MaxLength(16)] public string? Color { get; set; }
    public ICollection<ImageTag> ImageTags { get; set; } = new List<ImageTag>();

    /// <summary>工作流标签关联（Tag 表与图库共用同一标签池，工作流用 WorkflowTag 多对多关联）。</summary>
    public ICollection<WorkflowTag> WorkflowTags { get; set; } = new List<WorkflowTag>();
}

public class ImageTag
{
    public int ImageInfoId { get; set; }
    [ForeignKey(nameof(ImageInfoId))] public ImageInfo Image { get; set; } = null!;
    public int TagId { get; set; }
    [ForeignKey(nameof(TagId))] public Tag Tag { get; set; } = null!;
}

/// <summary>工作流-标签多对多关联（与图库 ImageTag 同模式：共用 Tag 表，Tag 池统一、抽屉一次管理）。</summary>
public class WorkflowTag
{
    public int WorkflowId { get; set; }
    [ForeignKey(nameof(WorkflowId))] public Workflow Workflow { get; set; } = null!;
    public int TagId { get; set; }
    [ForeignKey(nameof(TagId))] public Tag Tag { get; set; } = null!;
}

public class BlacklistedHash
{
    [Key, MaxLength(64)] public string Hash { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(1024)] public string? Reason { get; set; }
}

public class WorkflowInput
{
    [Key] public int Id { get; set; }
    public int WorkflowId { get; set; }
    [ForeignKey(nameof(WorkflowId))] public Workflow Workflow { get; set; } = null!;
    [Required, MaxLength(64)] public string NodeId { get; set; } = string.Empty;
    [Required, MaxLength(128)] public string FieldName { get; set; } = string.Empty;
    [MaxLength(256)] public string? DisplayName { get; set; }
    [MaxLength(32)] public string FieldType { get; set; } = "text";
    [MaxLength(4096)] public string? DefaultValue { get; set; }
    public int SortOrder { get; set; }

    /// <summary>逻辑删除标记（工作流逻辑删除时随其置位）。</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>
/// 单级文件夹（统一资产文件夹：工作流 / 提示词 / 图库，用 <see cref="Scope"/> 区分）。
/// 只支持一级（无父子层级）；资产与文件夹为多对多（PromptFolderMaps / WorkflowFolderMaps / GalleryFolderMaps）。
/// </summary>
public class Folder
{
    [Key] public int Id { get; set; }
    [Required, MaxLength(128)] public string Name { get; set; } = string.Empty;

    /// <summary>资产范围："Workflow" / "Prompt" / "Gallery"。</summary>
    [Required, MaxLength(16)] public string Scope { get; set; } = "Prompt";
    public int Sort { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>提示词关联（多对多）。</summary>
    public ICollection<PromptFolderMap> PromptFolderMaps { get; set; } = new List<PromptFolderMap>();
    /// <summary>工作流关联（多对多）。</summary>
    public ICollection<WorkflowFolderMap> WorkflowFolderMaps { get; set; } = new List<WorkflowFolderMap>();
    /// <summary>图库图片关联（多对多）。</summary>
    public ICollection<GalleryFolderMap> GalleryFolderMaps { get; set; } = new List<GalleryFolderMap>();
}

/// <summary>Workflow-Folder 多对多关联表（删除文件夹/删除工作流均级联清理关联）。</summary>
public class WorkflowFolderMap
{
    public int WorkflowId { get; set; }
    [ForeignKey(nameof(WorkflowId))] public Workflow Workflow { get; set; } = null!;
    public int FolderId { get; set; }
    [ForeignKey(nameof(FolderId))] public Folder Folder { get; set; } = null!;
}

/// <summary>GalleryImage-Folder 多对多关联表（删除文件夹/删除图片均级联清理关联）。</summary>
public class GalleryFolderMap
{
    public int ImageInfoId { get; set; }
    [ForeignKey(nameof(ImageInfoId))] public ImageInfo Image { get; set; } = null!;
    public int FolderId { get; set; }
    [ForeignKey(nameof(FolderId))] public Folder Folder { get; set; } = null!;
}

public class WorkflowJob
{
    [Key] public int Id { get; set; }
    public int WorkflowId { get; set; }
    [ForeignKey(nameof(WorkflowId))] public Workflow Workflow { get; set; } = null!;
    [Required] public string InputJson { get; set; } = string.Empty;
    [MaxLength(64)] public string? PromptId { get; set; }
    [MaxLength(16)] public string Status { get; set; } = "queued";
    public string? ErrorMessage { get; set; }

    /// <summary>实际开始执行时间（WS execution_start 到达时记录）。</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>GZip 压缩的节点执行日志快照（executing/progress/executed 事件序列，JSON 数组）。</summary>
    public byte[]? NodeLogBlob { get; set; }

    /// <summary>逻辑删除标记（工作流逻辑删除时随其置位）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public ICollection<JobOutput> Outputs { get; set; } = new List<JobOutput>();
}

public class JobOutput
{
    [Key] public int Id { get; set; }
    public int JobId { get; set; }
    [ForeignKey(nameof(JobId))] public WorkflowJob Job { get; set; } = null!;
    [Required, MaxLength(512)] public string FileName { get; set; } = string.Empty;
    [MaxLength(512)] public string? SubFolder { get; set; }

    /// <summary>本次执行关联的 ComfyUI prompt_id（便于溯源）。</summary>
    [MaxLength(64)] public string? PromptId { get; set; }

    public int? ImageInfoId { get; set; }
    [ForeignKey(nameof(ImageInfoId))] public ImageInfo? Image { get; set; }

    /// <summary>逻辑删除标记（工作流逻辑删除时随其置位）。</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>工作流参数配置（详情页"配置参数"弹窗保存，执行时覆盖 UI 工作流的 widgets_values 后提交）。</summary>
public class WorkflowParams
{
    [Key] public int Id { get; set; }
    public int WorkflowId { get; set; }
    [ForeignKey(nameof(WorkflowId))] public Workflow Workflow { get; set; } = null!;

    /// <summary>参数配置 JSON（WorkflowParamEntry[]：节点 id / 字段名 / 固定值或随机范围）。</summary>
    public string ParamsJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>工作流参数配置项（序列化进 WorkflowParams.ParamsJson）。</summary>
public class WorkflowParamEntry
{
    /// <summary>UI 节点 id（字符串，如 "458"）。</summary>
    public string NodeId { get; set; } = "";
    /// <summary>widget 输入名（如 seed / steps / cfg）。</summary>
    public string FieldName { get; set; } = "";
    /// <summary>固定值（字符串形式；数字/布尔会按目标类型转换，字符串原样）。</summary>
    public string? Value { get; set; }
    /// <summary>种子等数值字段支持随机：为 true 时忽略 Value，在 RandomMin~RandomMax 内取随机。</summary>
    public bool UseRandom { get; set; }

    /// <summary>随机范围下限（默认 0）。</summary>
    public ulong RandomMin { get; set; }

    /// <summary>随机范围上限（默认 2^64-1 = 18446744073709551615，ComfyUI Seed 全范围）。</summary>
    public ulong RandomMax { get; set; } = ulong.MaxValue;
}

#endregion

