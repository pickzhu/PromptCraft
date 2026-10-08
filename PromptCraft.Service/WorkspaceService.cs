using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using Ke.Bee.Localization.Localizer;

namespace PromptCraft.Service;

/// <summary>
/// 工作空间固定子目录清单（对齐 PromptMaster P 报告 5.2.8：
/// data/ covers/ uploads/ cache/ exports/ _caption_tmp/ datasets/ skills/）。
/// </summary>
public static class WorkspaceLayout
{
    public const string Data = "data";
    public const string Covers = "covers";
    public const string Uploads = "uploads";
    public const string Cache = "cache";
    public const string Exports = "exports";
    public const string CaptionTmp = "_caption_tmp";
    public const string Datasets = "datasets";
    public const string Skills = "skills";

    /// <summary>InitializeAsync 时一次性创建的全部子目录。</summary>
    public static readonly string[] SubDirectories =
    {
        Data, Covers, Uploads, Cache, Exports, CaptionTmp, Datasets, Skills,
    };
}

/// <summary>
/// 工作空间服务（PromptMaster 迁移 T0.1）。
/// 负责：路径白名单校验 + 子目录创建 + 把工作空间路径解析为各资源子路径。
/// 持久化走 settings.db 的 AppConfig.WorkspaceDir（见 <see cref="AppConfig"/>）。
/// </summary>
public class WorkspaceService : IWorkspaceService
{
    /// <summary>用户未配置 WorkspaceDir 时使用的默认根目录。</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PromptCraft", "workspace");

    /// <summary>当前生效的工作空间根目录（未配置时回退到默认目录）。</summary>
    public string Root { get; private set; } = string.Empty;

    /// <summary>
    /// 启动时初始化工作空间：读 AppConfig.WorkspaceDir，校验后创建 8 个子目录。
    /// 已配置但非法时不抛异常，回退到默认目录并记录（避免旧脏配置卡死启动）。
    /// </summary>
    public async Task<string> InitializeAsync(CancellationToken ct = default)
    {
        var configured = ConfigRepository.LoadFromDb()?.WorkspaceDir ?? string.Empty;
        string root;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IsValidRootPath(configured, out _))
            {
                root = Path.GetFullPath(configured);
            }
            else
            {
                // 持久化的路径非法（例如手工改坏 settings.db）→ 回退默认，不阻断启动
                root = DefaultRoot;
            }
        }
        else
        {
            root = DefaultRoot;
        }

        foreach (var sub in WorkspaceLayout.SubDirectories)
        {
            var dir = Path.Combine(root, sub);
            Directory.CreateDirectory(dir);
        }

        Root = root;
        await Task.CompletedTask;
        return root;
    }

    /// <summary>
    /// 用户在设置页切换工作空间时调用：先校验路径，再创建子目录。
    /// 校验失败抛 <see cref="ArgumentException"/>（UI 直接展示 message）。
    /// </summary>
    public async Task<string> InitializeAsync(string workspaceDir, CancellationToken ct = default)
    {
        if (!IsValidRootPath(workspaceDir, out var error))
            throw new ArgumentException(error, nameof(workspaceDir));

        var root = Path.GetFullPath(workspaceDir);
        foreach (var sub in WorkspaceLayout.SubDirectories)
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
        }

        Root = root;
        await Task.CompletedTask;
        return root;
    }

    /// <summary>
    /// 路径白名单（对齐 PromptMaster P 报告 5.4）：
    /// 必须是盘符根的绝对路径；每段仅允许 [A-Za-z0-9_-]；禁空格/中文/UNC。
    /// </summary>
    public static bool IsValidRootPath(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = Localizer.Instance?["WorkspaceEmpty"] ?? "";
            return false;
        }

        path = path.Trim();

        // 禁 UNC（\\server\share 或 //server/share）
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            error = Localizer.Instance?["WorkspaceUncNotSupported"] ?? "";
            return false;
        }

        if (!Path.IsPathRooted(path))
        {
            error = Localizer.Instance?["WorkspaceMustBeAbsolute"] ?? "";
            return false;
        }

        var parts = path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        // 至少要有盘符 + 一层目录
        if (parts.Length < 2)
        {
            error = Localizer.Instance?["WorkspacePathTooShallow"] ?? "";
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var seg = parts[i];

            // 盘符段形如 "E:"
            if (i == 0 && seg.EndsWith(":", StringComparison.Ordinal))
            {
                if (seg.Length != 2 || !char.IsAsciiLetter(seg[0]))
                {
                    error = string.Format(Localizer.Instance?["WorkspaceDriveInvalid"] ?? "", seg);
                    return false;
                }
                continue;
            }

            if (seg == "." || seg == "..")
            {
                error = Localizer.Instance?["WorkspaceDotNotAllowed"] ?? "";
                return false;
            }

            foreach (var ch in seg)
            {
                var ok = (ch is >= 'a' and <= 'z')
                      || (ch is >= 'A' and <= 'Z')
                      || (ch is >= '0' and <= '9')
                      || ch == '_' || ch == '-';
                if (!ok)
                {
                    error = string.Format(Localizer.Instance?["WorkspaceSegmentInvalid"] ?? "", seg);
                    return false;
                }
            }
        }

        return true;
    }

    // ---- 子路径解析（Root 未初始化时按当前配置即时解析） ----

    private string ResolveRoot()
    {
        if (!string.IsNullOrEmpty(Root)) return Root;
        var configured = ConfigRepository.LoadFromDb()?.WorkspaceDir;
        return IsValidRootPath(configured, out _) ? Path.GetFullPath(configured!) : DefaultRoot;
    }

    public string DataDir => Path.Combine(ResolveRoot(), WorkspaceLayout.Data);
    public string CoversDir => Path.Combine(ResolveRoot(), WorkspaceLayout.Covers);
    public string UploadsDir => Path.Combine(ResolveRoot(), WorkspaceLayout.Uploads);
    public string CacheDir => Path.Combine(ResolveRoot(), WorkspaceLayout.Cache);
    public string ExportsDir => Path.Combine(ResolveRoot(), WorkspaceLayout.Exports);
    public string CaptionTmpDir => Path.Combine(ResolveRoot(), WorkspaceLayout.CaptionTmp);
    public string DatasetsDir => Path.Combine(ResolveRoot(), WorkspaceLayout.Datasets);
    public string SkillsDir => Path.Combine(ResolveRoot(), WorkspaceLayout.Skills);
}
