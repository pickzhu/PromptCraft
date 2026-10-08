using PromptCraft.Models;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Data;

/// <summary>
/// 全局环境配置专用的 DbContext，固定路径 %APPDATA%/PromptCraft/settings.db。
/// 只放启动期就要读的环境/引用配置（主题、语言、日志级别、ComfyUI 地址、工作空间目录）。
/// 业务数据（AI 提供商、工作流、图片、提示词）走 ComfyDbContext / PromptLibraryDbContext。
/// </summary>
public class SettingsDbContext : DbContext
{
    public DbSet<AppConfig> AppConfig => Set<AppConfig>();

    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PromptCraft", "settings.db");

    public SettingsDbContext() { }

    public SettingsDbContext(DbContextOptions<SettingsDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            optionsBuilder.UseSqlite($"Data Source={DbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AppConfig>(e =>
        {
            e.ToTable("AppConfig");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); // 固定 ID=1
            e.Property(x => x.IsLight).HasConversion<int>();
            e.Property(x => x.BackgroundAnimations).HasConversion<int>();
            e.Property(x => x.BackgroundTransitions).HasConversion<int>();
            e.Property(x => x.ShowBottomBar).HasConversion<int>();
            e.Property(x => x.ShowTitleBar).HasConversion<int>();
            e.Property(x => x.ThumbMaxDimension).HasDefaultValue(300);
            e.Property(x => x.ThumbQuality).HasDefaultValue(80);
            e.Property(x => x.LogLevel).HasConversion<int>().HasDefaultValue(PromptCraft.Models.LogLevel.Info);
        });
    }
}
