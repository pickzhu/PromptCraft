namespace PromptCraft.Consts.Event
{
    /// <summary>
    /// 用于存放各种事件名称
    /// </summary>
    public class EventNameConst
    {
        public const string SystemLangueChageEvent = "SYSTEM_LANGUE_CHANGUE_EVENT";
        public const string CustomAddThemColorUnloadEvent = "CUSTOM_ADD_THEM_COLOR_UNLOAD_EVENT";
        public const string CustomAddThemColorDataEvent = "CUSTOM_ADD_THEM_COLOR_DATA_EVENT";
        public const string SystemMainWindowUnloadedEvent = "SYSTEM_MAIN_WINDOW_UNLOADED_EVENT";
        public const string SystemOpenUrlEvent = "SYSTEM_OPEN_URL_EVENT";

        /// <summary>词库数据变更（提示词/文件夹/标签增删改）后发布，词库页面订阅刷新。</summary>
        public const string PromptLibraryChangedEvent = "PROMPT_LIBRARY_CHANGED_EVENT";

        /// <summary>AI 提供商/模型增删改后发布，扩写/反推等持有提供商列表的页面订阅刷新。</summary>
        public const string ProviderChangedEvent = "PROVIDER_CHANGED_EVENT";

        /// <summary>提示词工程（PE）增删改/启用开关变化后发布，扩写/反推等持有 PE 下拉的页面订阅刷新。</summary>
        public const string PromptEngineeringChangedEvent = "PROMPT_ENGINEERING_CHANGED_EVENT";

        /// <summary>请求主窗口切换到指定页面（payload 为页面 ViewModel 的 Type；用于子页内"去设置"等跳转）。</summary>
        public const string SystemNavigatePageEvent = "SYSTEM_NAVIGATE_PAGE_EVENT";
    }
}
