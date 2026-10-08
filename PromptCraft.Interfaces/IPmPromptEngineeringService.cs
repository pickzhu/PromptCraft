using PromptCraft.Models.Inference;

namespace PromptCraft.Interfaces;

/// <summary>
/// 提示词工程（Prompt Engineering）服务。
/// 1:1 移植自 app/electron/service/pm_prompt_engineering.js + config/promptEngineeringRegistry.js
/// + promptEngineeringResolver.js + promptEngineeringTaxonomy.js。
/// </summary>
public interface IPmPromptEngineeringService
{
    /// <summary>保存全部自定义 PE（整体落库/落文件）。</summary>
    void SaveStore(IEnumerable<PromptEngineeringProfile> profiles);

    /// <summary>列出 PE（可按 kind 过滤；enabledOnly=true 只返回启用项）。</summary>
    List<PromptEngineeringProfile> ListProfiles(string? kind = null, bool enabledOnly = false);

    /// <summary>取单个 PE。</summary>
    PromptEngineeringProfile? GetProfile(string? id);

    /// <summary>保存/新建 PE（内置 PE 禁编辑/删除）。</summary>
    PromptEngineeringProfile? SaveProfile(PromptEngineeringProfile profile);

    /// <summary>启用/停用 PE。</summary>
    void SetProfileEnabled(string? id, bool enabled);

    /// <summary>删除 PE（内置 PE 拒绝删除）。</summary>
    void DeleteProfile(string? id);

    /// <summary>导出 PE 为 JSON 字符串。</summary>
    string ExportProfile(string? id);

    /// <summary>校验导入文件。</summary>
    (bool Valid, string FileName, string? ProfileName, string? ProfileKind, string Message) ValidateImportFile(string? filePath);

    /// <summary>导入 PE（返回导入后的 profile）。</summary>
    PromptEngineeringProfile ImportProfile(string? filePath);

    /// <summary>把所选 PE 映射到反推请求（核心入口）。</summary>
    ReverseCaptionRequest ApplyReverseToCaption(ReverseCaptionRequest caption, string? peId);

    /// <summary>按 kind 取默认用户模板。</summary>
    string GetDefaultUserTemplate(string? kind);
}
