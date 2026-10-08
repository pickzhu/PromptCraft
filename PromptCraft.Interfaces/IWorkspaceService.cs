namespace PromptCraft.Interfaces;

/// <summary>工作空间服务（PromptMaster 迁移 T0.1）：路径白名单校验 + 子目录创建 + 资源子路径解析。</summary>
public interface IWorkspaceService
{
    /// <summary>当前生效的工作空间根目录（未配置时回退到默认目录）。</summary>
    string Root { get; }

    /// <summary>启动时初始化工作空间：读 AppConfig.WorkspaceDir，校验后创建 8 个子目录。已配置但非法时回退默认目录。</summary>
    Task<string> InitializeAsync(CancellationToken ct = default);

    /// <summary>用户在设置页切换工作空间时调用：先校验路径，再创建子目录。校验失败抛 ArgumentException。</summary>
    Task<string> InitializeAsync(string workspaceDir, CancellationToken ct = default);

    string DataDir { get; }
    string CoversDir { get; }
    string UploadsDir { get; }
    string CacheDir { get; }
    string ExportsDir { get; }
    string CaptionTmpDir { get; }
    string DatasetsDir { get; }
    string SkillsDir { get; }
}
