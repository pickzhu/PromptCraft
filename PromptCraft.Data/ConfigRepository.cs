using BaseClassLib.Models;
using PromptCraft.Models;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace PromptCraft.Data;

/// <summary>
/// SQLite 配置仓库，通过 SettingsDbContext（EF Core）访问。
/// 静态方法可在 DI 就绪前使用；实例方法供 DI 注入。
/// 本类不依赖 PromptCraft.Service：日志通过 <see cref="WarningLog"/> 回调桥接（由应用启动时注入 LogService）。
/// </summary>
public class ConfigRepository
{
    /// <summary>静态日志桥接：由应用启动时注入（App.Initialize → LogService.Instance），避免 Data 层反向依赖 Service 层。</summary>
    public static Action<string, Exception?>? WarningLog { get; set; }

    // ===== 静态：启动阶段可用（无 DI） =====

    /// <summary>确保数据库和表存在（幂等）；兼容旧库：自动补充新增列</summary>
    public static void EnsureDatabase()
    {
        using var db = new SettingsDbContext();
        db.Database.EnsureCreated();
        // 兼容旧库：早期版本没有 LogLevel 列，幂等补充（SQLite ADD COLUMN 带默认值）
        try
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE AppConfig ADD COLUMN LogLevel INTEGER NOT NULL DEFAULT 2");
        }
        catch
        {
            // 列已存在，忽略
        }
        // T0.1：旧库补 WorkspaceDir 列（新库由 EnsureCreated 建好）
        try
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE AppConfig ADD COLUMN WorkspaceDir TEXT NOT NULL DEFAULT ''");
        }
        catch
        {
            // 列已存在，忽略
        }
        // T0.4：翻译引擎 + 百度 appId/appKey
        try { db.Database.ExecuteSqlRaw("ALTER TABLE AppConfig ADD COLUMN TranslateEngine TEXT NOT NULL DEFAULT 'openai'"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE AppConfig ADD COLUMN BaiduAppId TEXT NOT NULL DEFAULT ''"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE AppConfig ADD COLUMN BaiduAppKey TEXT NOT NULL DEFAULT ''"); } catch { }

        // T0.7 迁移：provider_configs/provider_models 已从 settings.db 迁到 comfy.db。
        // 旧库残留表直接 DROP（数据已在 EnsureComfyDatabaseSchema 阶段迁走）。
        try { db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS provider_models"); } catch { }
        try { db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS provider_configs"); } catch { }
    }

    /// <summary>从 DB 加载配置；返回 null 表示无记录</summary>
    public static AppConfig? LoadFromDb()
    {
        using var db = new SettingsDbContext();
        return db.AppConfig.Find(1);
    }

    /// <summary>保存完整配置到 DB（UPSERT）</summary>
    public static void SaveToDb(AppConfig config)
    {
        using var db = new SettingsDbContext();
        config.Id = 1;

        var existing = db.AppConfig.Find(1);
        if (existing != null)
        {
            // T0.1：WorkspaceDir 不在旧版 BaseClassLib.Models.Config 桥接范围内，
            // 经 FromJsonConfig 保存其它设置时 WorkspaceDir 会是空串——此时保留已持久化的值，
            // 避免设置页保存其它卡片时把用户配的工作空间抹掉。
            if (string.IsNullOrEmpty(config.WorkspaceDir) && !string.IsNullOrEmpty(existing.WorkspaceDir))
            {
                config.WorkspaceDir = existing.WorkspaceDir;
            }
            // T0.4：翻译引擎/百度配置不在 JSON Config 桥接范围，关闭应用时 MainViewModel
            // 触发的 SaveConfig 会用 FromJsonConfig 覆盖 AppConfig——这里保留 DB 已持久化值，
            // 避免被关窗时的全局保存冲掉。
            config.TranslateEngine = existing.TranslateEngine;
            config.BaiduAppId = existing.BaiduAppId;
            config.BaiduAppKey = existing.BaiduAppKey;
            db.Entry(existing).CurrentValues.SetValues(config);
        }
        else
        {
            db.AppConfig.Add(config);
        }

        db.SaveChanges();
    }

    /// <summary>将 JSON Config 转换为 AppConfig（含 AddedThemeColors 序列化）</summary>
    public static AppConfig FromJsonConfig(BaseClassLib.Models.Config json)
    {
        var addedColorsJson = json.AddedThemeColors?.Any() == true
            ? JsonSerializer.Serialize(json.AddedThemeColors)
            : null;

        return new AppConfig
        {
            IsLight = json.IsLight,
            BackgroundAnimations = json.BackgroundAnimations,
            BackgroundStyle = json.BackgroundStyle,
            BackgroundTransitions = json.BackgroundTransitions,
            ShowBottomBar = json.ShowBottomBar,
            ShowTitleBar = json.ShowTitleBar,
            Language = json.Language,
            SukiColorThemeName = json.SukiColorThemeName,
            AddedThemeColorsJson = addedColorsJson,
            ComfyApiUrl = json.ComfyApiUrl,
            ComfyOutputDir = json.ComfyOutputDir,
            ThumbMaxDimension = json.ThumbMaxDimension,
            ThumbQuality = json.ThumbQuality,
            LogLevel = (LogLevel)json.LogLevel
        };
    }

    /// <summary>AppConfig 转回 JSON Config（供 AppInitData.Config 兼容）</summary>
    public static BaseClassLib.Models.Config ToJsonConfig(AppConfig appConfig)
    {
        var cfg = new BaseClassLib.Models.Config
        {
            IsLight = appConfig.IsLight,
            BackgroundAnimations = appConfig.BackgroundAnimations,
            BackgroundStyle = appConfig.BackgroundStyle,
            BackgroundTransitions = appConfig.BackgroundTransitions,
            ShowBottomBar = appConfig.ShowBottomBar,
            ShowTitleBar = appConfig.ShowTitleBar,
            Language = appConfig.Language,
            SukiColorThemeName = appConfig.SukiColorThemeName,
            AddedThemeColors = null,
            ComfyApiUrl = appConfig.ComfyApiUrl,
            ComfyOutputDir = appConfig.ComfyOutputDir,
            ThumbMaxDimension = appConfig.ThumbMaxDimension,
            ThumbQuality = appConfig.ThumbQuality,
            LogLevel = (int)appConfig.LogLevel
        };

        if (!string.IsNullOrEmpty(appConfig.AddedThemeColorsJson))
        {
            try
            {
                cfg.AddedThemeColors = JsonSerializer.Deserialize<List<AddThemeModel>>(appConfig.AddedThemeColorsJson);
            }
            catch (Exception ex)
            {
                WarningLog?.Invoke(Localizer.Instance?["ThemeColorParseFailed"] ?? "ThemeColorParseFailed", ex);
            }
        }

        return cfg;
    }

    /// <summary>从 JSON 迁移到 DB（如果 DB 没有记录）</summary>
    public static void MigrateFromJson(BaseClassLib.Models.Config? jsonConfig)
    {
        EnsureDatabase();
        if (LoadFromDb() != null) return; // 已有记录

        if (jsonConfig == null)
        {
            SaveToDb(new AppConfig());
            return;
        }

        var appConfig = FromJsonConfig(jsonConfig);
        SaveToDb(appConfig);
    }

    // ===== 实例：DI 注入用 =====

    private readonly SettingsDbContext _db;
    public ConfigRepository(SettingsDbContext db) { _db = db; }

    /// <summary>从 DB 加载（实例方法）</summary>
    public AppConfig? Load() => _db.AppConfig.Find(1);

    /// <summary>保存到 DB（实例方法）</summary>
    public void Save(AppConfig config)
    {
        config.Id = 1;
        var existing = _db.AppConfig.Find(1);
        if (existing != null)
            _db.Entry(existing).CurrentValues.SetValues(config);
        else
            _db.AppConfig.Add(config);
        _db.SaveChanges();
    }
}