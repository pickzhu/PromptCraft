namespace PromptCraft.Models;

/// <summary>
/// 全局业务 KV 配置（comfyui.db），对齐 PromptMaster settingOperation（key/value）。
/// 用途：扩写参数记忆（pm_expand_*）、PE 偏好、模型记忆等业务级配置。
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
