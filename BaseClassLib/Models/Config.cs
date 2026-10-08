using SukiUI.Enums;

namespace BaseClassLib.Models
{
    public class Config
    {
        public bool IsLight { get; set; }
        public bool BackgroundAnimations { get; set; }
        public SukiBackgroundStyle BackgroundStyle { get; set; }
        public bool BackgroundTransitions { get; set; }
        public bool ShowBottomBar { get; set; }
        public bool ShowTitleBar { get; set; }
        public LanguageEnum Language { get; set; }
        public string? SukiColorThemeName { get; set; }
        public List<AddThemeModel>? AddedThemeColors { get; set; }

        // ComfyUI 配置
        public string ComfyApiUrl { get; set; } = "http://127.0.0.1:8188";
        public string ComfyOutputDir { get; set; } = string.Empty;
        public int ThumbMaxDimension { get; set; } = 300;
        public int ThumbQuality { get; set; } = 80;

        // 日志级别（对应 PromptCraft.Interfaces.LogLevel 的数值，2=Info；
        // 用 int 存储以保持 BaseClassLib 不依赖 Interfaces）
        public int LogLevel { get; set; } = 2;
    }
}

