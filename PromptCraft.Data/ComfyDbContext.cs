using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Data;

public class ComfyDbContext : DbContext
{
    public DbSet<ImageInfo> ImageMetadata => Set<ImageInfo>();
    public DbSet<ImageStatus> ImageStatuses => Set<ImageStatus>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ImageTag> ImageTags => Set<ImageTag>();
    public DbSet<WorkflowTag> WorkflowTags => Set<WorkflowTag>();
    public DbSet<BlacklistedHash> BlacklistedHashes => Set<BlacklistedHash>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowInput> WorkflowInputs => Set<WorkflowInput>();
    public DbSet<WorkflowJob> WorkflowJobs => Set<WorkflowJob>();
    public DbSet<JobOutput> JobOutputs => Set<JobOutput>();
    public DbSet<WorkflowParams> WorkflowParamsSet => Set<WorkflowParams>();

    // 图片提取的提示词（拆表存储，去重复用）
    public DbSet<ImagePrompt> ImagePrompts => Set<ImagePrompt>();

    // AI 提供商（settings.db 只放全局环境配置）
    public DbSet<ProviderConfig> ProviderConfigs => Set<ProviderConfig>();
    public DbSet<ProviderModel> ProviderModels => Set<ProviderModel>();

    // 全局业务 KV 配置（扩写参数记忆等；对齐 PromptMaster settingOperation）
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    // 自定义提示词工程（对齐 prompt_engineering.json customOnly 条目；kind 含 expand/reverse/train）
    public DbSet<PromptCraft.Models.Inference.PromptEngineeringProfile> PromptEngineeringProfiles
        => Set<PromptCraft.Models.Inference.PromptEngineeringProfile>();

    // 统一单级文件夹（工作流/提示词/图库，Scope 区分）
    public DbSet<Folder> Folders => Set<Folder>();

    // 提示词库（原 library.sqlite 并入本库；表/列大驼峰）
    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<PromptTagMap> PromptTagMaps => Set<PromptTagMap>();

    // 资产-文件夹多对多关联（统一 Folder 表；Prompt 用字符串 Id，Workflow/Gallery 用 int Id）
    public DbSet<PromptFolderMap> PromptFolderMaps => Set<PromptFolderMap>();
    public DbSet<WorkflowFolderMap> WorkflowFolderMaps => Set<WorkflowFolderMap>();
    public DbSet<GalleryFolderMap> GalleryFolderMaps => Set<GalleryFolderMap>();

    public ComfyDbContext(DbContextOptions<ComfyDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ImageInfo>(e =>
        {
            e.HasIndex(x => x.Hash).IsUnique().HasFilter("[Hash] IS NOT NULL");
            e.HasIndex(x => x.RelativePath).IsUnique();
            e.HasIndex(x => x.WorkflowId);
            e.HasIndex(x => x.PromptId);
            e.HasOne(x => x.Status).WithOne(x => x.Image).HasForeignKey<ImageStatus>(x => x.ImageInfoId);
            e.HasOne(x => x.Workflow).WithMany(x => x.Images).HasForeignKey(x => x.WorkflowId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Prompt).WithMany(x => x.Images).HasForeignKey(x => x.PromptId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.FolderMaps).WithOne(x => x.Image).HasForeignKey(x => x.ImageInfoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 图片提取的提示词：同一工作流内 PromptHash 唯一 → 去重复用
        model.Entity<ImagePrompt>(e =>
        {
            e.HasIndex(x => new { x.WorkflowId, x.PromptHash }).IsUnique();
            e.HasIndex(x => x.WorkflowId);
        });

        model.Entity<Tag>(e =>
        {
            e.Property(x => x.Color).HasMaxLength(16);
        });

        model.Entity<ImageTag>(e =>
        {
            e.HasKey(x => new { x.ImageInfoId, x.TagId });
            e.HasOne(x => x.Image).WithMany(x => x.ImageTags).HasForeignKey(x => x.ImageInfoId);
            e.HasOne(x => x.Tag).WithMany(x => x.ImageTags).HasForeignKey(x => x.TagId);
        });

        model.Entity<WorkflowTag>(e =>
        {
            e.HasKey(x => new { x.WorkflowId, x.TagId });
            e.HasOne(x => x.Workflow).WithMany(x => x.WorkflowTags).HasForeignKey(x => x.WorkflowId);
            e.HasOne(x => x.Tag).WithMany(x => x.WorkflowTags).HasForeignKey(x => x.TagId);
            e.HasIndex(x => x.TagId);
        });

        model.Entity<WorkflowInput>(e =>
        {
            e.HasOne(x => x.Workflow).WithMany(x => x.Inputs).HasForeignKey(x => x.WorkflowId);
        });

        // 统一单级文件夹：同 Scope 内名称唯一（忽略大小写）；删除文件夹/删除资产时关联表级联清理
        model.Entity<Folder>(e =>
        {
            e.HasIndex(x => x.Scope);
            e.HasIndex(x => new { x.Scope, x.Name }).IsUnique();
            e.HasMany(x => x.PromptFolderMaps).WithOne(x => x.Folder).HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.WorkflowFolderMaps).WithOne(x => x.Folder).HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.GalleryFolderMaps).WithOne(x => x.Folder).HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 资产-文件夹多对多关联表：联合主键 + FolderId 索引（文件夹→资产查询）
        model.Entity<PromptFolderMap>(e =>
        {
            e.ToTable("PromptFolderMaps");
            e.HasKey(x => new { x.PromptId, x.FolderId });
            e.HasIndex(x => x.FolderId);
        });
        model.Entity<WorkflowFolderMap>(e =>
        {
            e.ToTable("WorkflowFolderMaps");
            e.HasKey(x => new { x.WorkflowId, x.FolderId });
            e.HasIndex(x => x.FolderId);
        });
        model.Entity<GalleryFolderMap>(e =>
        {
            e.ToTable("GalleryFolderMaps");
            e.HasKey(x => new { x.ImageInfoId, x.FolderId });
            e.HasIndex(x => x.FolderId);
        });

        model.Entity<Workflow>(e =>
        {
            e.Property(x => x.WorkflowJsonBlob).HasColumnType("BLOB");
            e.HasIndex(x => x.SourcePath).IsUnique().HasFilter("[SourcePath] IS NOT NULL");
            e.HasIndex(x => x.WorkflowGuid).IsUnique().HasFilter("[WorkflowGuid] IS NOT NULL");
            e.HasMany(x => x.Prompts).WithOne(x => x.Workflow).HasForeignKey(x => x.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.IsDeleted);
            e.HasMany(x => x.FolderMaps).WithOne(x => x.Workflow).HasForeignKey(x => x.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WorkflowJob>(e =>
        {
            // 必须配对 Workflow.Jobs 集合导航，否则 EF 会为 WorkflowJob 推断出第二条关系
            // （影子外键 WorkflowId1），删除时 JOIN 引用不存在的列而报错
            e.HasOne(x => x.Workflow).WithMany(x => x.Jobs).HasForeignKey(x => x.WorkflowId);
            e.HasIndex(x => x.Status);
        });

        model.Entity<JobOutput>(e =>
        {
            e.HasOne(x => x.Job).WithMany(x => x.Outputs).HasForeignKey(x => x.JobId);
            e.HasOne(x => x.Image).WithMany().HasForeignKey(x => x.ImageInfoId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<WorkflowParams>(e =>
        {
            // 每个工作流至多一份参数配置（按 WorkflowId 唯一）
            e.ToTable("WorkflowParams");
            e.HasIndex(x => x.WorkflowId).IsUnique();
            e.HasOne(x => x.Workflow).WithMany().HasForeignKey(x => x.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AI 提供商（表名大驼峰）
        model.Entity<ProviderConfig>(e =>
        {
            e.ToTable("ProviderConfigs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Enabled).HasConversion<int>();
            e.HasMany(x => x.Models).WithOne().HasForeignKey(x => x.ProviderId);
        });
        model.Entity<ProviderModel>(e =>
        {
            e.ToTable("ProviderModels");
            e.HasKey(x => x.Id);
            e.Property(x => x.UseForExpand).HasConversion<int>();
            e.Property(x => x.UseForReverse).HasConversion<int>();
            e.Property(x => x.IsDefaultExpand).HasConversion<int>();
            e.Property(x => x.IsDefaultReverse).HasConversion<int>();
            e.HasIndex(x => x.ProviderId);
        });

        // 全局业务 KV（Key 主键；对齐 PromptMaster settingOperation）
        model.Entity<AppSetting>(e =>
        {
            e.ToTable("AppSettings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(4096);
        });

        // 自定义提示词工程（Id 主键；对齐 prompt_engineering.json customOnly 条目，内置注册表代码内维护，
        // 表仅落 customOnly 与 builtinOverrides 两段；Tags/SubjectDomains 以 JSON 文本列存储）
        model.Entity<PromptCraft.Models.Inference.PromptEngineeringProfile>(e =>
        {
            e.ToTable("PromptEngineeringProfiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.OutputFormat).HasMaxLength(50);
            e.Property(x => x.BuiltinKey).HasMaxLength(100);
            e.Property(x => x.CaptionType).HasMaxLength(100);
            e.Property(x => x.ToriiExtractMode).HasMaxLength(50);
            e.Property(x => x.Tags).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>());
            e.Property(x => x.SubjectDomains).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>());
            e.HasIndex(x => x.Kind);
        });

        // 提示词库（原 library.sqlite 并入；表/列大驼峰）
        model.Entity<Prompt>(e =>
        {
            e.ToTable("Prompts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Positive).HasMaxLength(16384);
            e.Property(x => x.Negative).HasMaxLength(16384);
            e.Property(x => x.Cover).HasMaxLength(512);
            e.Property(x => x.Note).HasMaxLength(2048);
            e.Property(x => x.Seed).HasMaxLength(64);
            e.Property(x => x.Models).HasMaxLength(1024);
            e.HasMany(x => x.FolderMaps).WithOne(x => x.Prompt).HasForeignKey(x => x.PromptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PromptTagMap>(e =>
        {
            e.ToTable("PromptTagMaps");
            e.HasKey(x => new { x.PromptId, x.TagId });
            e.HasIndex(x => x.TagId);
            e.HasOne<Prompt>().WithMany(x => x.PromptTags).HasForeignKey(x => x.PromptId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
