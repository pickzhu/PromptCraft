using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using BaseClassLib;
using BaseClassLib.Extends;
using BaseClassLib.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Common;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using PromptCraft.StaticData;
using Ke.Bee.Localization.Localizer;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using SukiUI;
using SukiUI.Controls;
using SukiUI.Dialogs;
using SukiUI.Enums;
using SukiUI.MessageBox;
using SukiUI.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System
{
    public partial class SettingModel : ViewModelBase, ISukiStackPageTitleProvider
    {
        public Action<SukiBackgroundStyle>? BackgroundStyleChanged { get; set; }
        public Action<bool>? BackgroundAnimationsChanged { get; set; }
        public Action<bool>? BackgroundTransitionsChanged { get; set; }
        public string Themeing { get; private set; }

        [ObservableProperty] private bool _isLightTheme;
        [ObservableProperty] private bool _backgroundAnimations;
        [ObservableProperty] private ListItem<SukiBackgroundStyle>? _backgroundStyle;
        [ObservableProperty] private bool _backgroundTransitions;
        [ObservableProperty] private IAvaloniaReadOnlyList<ListItem<SukiBackgroundStyle>>? _availableBackgroundStyles;
        [ObservableProperty] private ListItem<LanguageEnum> _currentLangue;
        [ObservableProperty] private ListItem<LogLevel> _selectedLogLevel;

    // ComfyUI 配置
    [ObservableProperty] private string _comfyApiUrl = string.Empty;
    [ObservableProperty] private string _comfyOutputDir = string.Empty;

    partial void OnComfyApiUrlChanged(string value) { if (!_loadingSettings) AutoSaveComfy(); }
    partial void OnComfyOutputDirChanged(string value) { if (!_loadingSettings) AutoSaveComfy(); }

    private void AutoSaveComfy()
    {
        if (AppInitData.Config == null) return;
        AppInitData.Config.ComfyApiUrl = ComfyApiUrl ?? "";
        AppInitData.Config.ComfyOutputDir = ComfyOutputDir ?? "";
        try { AppInitData.SaveConfig(); } catch { }
    }
    [ObservableProperty] private string _comfyConnectionStatus = string.Empty;
    [ObservableProperty] private bool _comfyIsConnected;

    // T0.1 工作空间目录（全局环境配置，落 settings.db AppConfig.WorkspaceDir）
    [ObservableProperty] private string _workspaceDir = string.Empty;
    [ObservableProperty] private string _workspaceStatus = string.Empty;

    partial void OnWorkspaceDirChanged(string value)
    {
        if (_loadingSettings) return;
        try
        {
            if (!PromptCraft.Service.WorkspaceService.IsValidRootPath(value ?? "", out _)) return;
            var current = PromptCraft.Data.ConfigRepository.LoadFromDb() ?? new PromptCraft.Models.AppConfig();
            current.WorkspaceDir = (value ?? "").Trim();
            PromptCraft.Data.ConfigRepository.SaveToDb(current);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error(Localizer.Instance?["AutoSaveWorkspaceFailed"] ?? "", "Setting", ex);
        }
    }

    // ---- T0.4 翻译引擎 ----
    [ObservableProperty] private string _translateEngine = "openai";
    [ObservableProperty] private string _baiduAppId = string.Empty;
    [ObservableProperty] private string _baiduAppKey = string.Empty;
    [ObservableProperty] private string _translateStatus = string.Empty;
    public string[] EngineItems { get; } = new[] { "openai", "baidu" };

    /// <summary>是否正在编辑/新增提供商（控制编辑表单可见性）。</summary>
    [ObservableProperty] private bool _isEditingProvider;

    /// <summary>引擎是否为百度（控制 AppId/AppKey 行的可见性）。</summary>
    public bool IsBaiduEngine => string.Equals(TranslateEngine, "baidu", StringComparison.OrdinalIgnoreCase);

    partial void OnTranslateEngineChanged(string value)
    {
        OnPropertyChanged(nameof(IsBaiduEngine));
        if (!_loadingSettings) AutoSaveTranslate();
    }
    partial void OnBaiduAppIdChanged(string value) { if (!_loadingSettings) AutoSaveTranslate(); }
    partial void OnBaiduAppKeyChanged(string value) { if (!_loadingSettings) AutoSaveTranslate(); }

    private void AutoSaveTranslate()
    {
        try
        {
            var current = PromptCraft.Data.ConfigRepository.LoadFromDb() ?? new PromptCraft.Models.AppConfig();
            current.TranslateEngine = TranslateEngine?.Trim().ToLowerInvariant() == "baidu" ? "baidu" : "openai";
            current.BaiduAppId = BaiduAppId?.Trim() ?? string.Empty;
            current.BaiduAppKey = BaiduAppKey?.Trim() ?? string.Empty;
            PromptCraft.Data.ConfigRepository.SaveToDb(current);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error(Localizer.Instance?["AutoSaveTranslateFailed"] ?? "", "Setting", ex);
        }
    }

    // ---- T0.7 AI 提供商（CherryIN 风格：模型列表逐行挑选） ----
    public ObservableCollection<ProviderConfig> Providers { get; } = new();
    /// <summary>当前正在编辑的提供商（null=未在编辑）。</summary>
    [ObservableProperty] private ProviderConfig? _selectedProvider;
    [ObservableProperty] private string _providerEditName = string.Empty;
    [ObservableProperty] private string _providerEditBaseUrl = string.Empty;
    [ObservableProperty] private string _providerEditApiKey = string.Empty;
    [ObservableProperty] private bool _providerApiKeyVisible;
    [ObservableProperty] private bool _providerEditEnabled = true;
    /// <summary>已加入的模型行（用户逐行挑选）。</summary>
    public ObservableCollection<ProviderModelRow> EditableRows { get; } = new();
    /// <summary>拉取到的全部模型池（供每行下拉选择，不自动加行）。</summary>
    public ObservableCollection<string> AvailableModelsPool { get; } = new();

    /// <summary>用户在下拉框里手输了一个模型名，把它补进下拉池（已存在则不动）。</summary>
    public void EnsureModelInPool(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !AvailableModelsPool.Contains(name))
            AvailableModelsPool.Add(name);
    }
    [ObservableProperty] private string _providerTestStatus = string.Empty;
    private string? _editingProviderId; // null = 新增

        private readonly SukiTheme _theme = SukiTheme.GetInstance();
        private readonly ISukiDialogManager _dialogManager;
        private readonly IServiceProvider _service;
        private readonly IBaseLogService _log;
        /// <summary>构造初始化阶段标志：初始化 SelectedLogLevel 不应触发持久化（避免覆盖数据库里的级别）。</summary>
        private bool _initializingLogLevel;
        /// <summary>构造函数加载初始配置期间为 true，避免 partial OnChanged 误触发 AutoSave 把 DB 里的值覆盖成空。</summary>
        private bool _loadingSettings = true;
        public IAvaloniaReadOnlyList<SukiColorTheme> AvailableColors { get; }
        public IAvaloniaReadOnlyList<ListItem<LanguageEnum>> LanguageItems { get; }

        /// <summary>日志输出级别选项</summary>
        public IAvaloniaReadOnlyList<ListItem<LogLevel>> AvailableLogLevels { get; } =
            new AvaloniaList<ListItem<LogLevel>>(
                Enum.GetValues<LogLevel>().Select(x => new ListItem<LogLevel>(x, x.ToString(), (int)x)));
        private SukiColorTheme? _addColor;
        private CustomThemColorModel _custom;

        public string Title => Themeing;

        public SettingModel(ILocalizer localizer, IBaseNotice baseNotice, ISukiDialogManager dialogManager, IServiceProvider service, IBaseLogService log) : base(localizer, baseNotice)
        {
            LanguageItems = new AvaloniaList<ListItem<LanguageEnum>>(AppInitData.Langues);
            _service = service;
            _log = log;
            ComfyConnectionStatus = _local["NotConnected"];
            ///这里有问题，会一直进入构造函数
            //_models = service.GetServices<ModelBase>();
            _displayName = "SETTING";
            _icon = MaterialIconKind.Cab;
            _index = 100;
            _sideMenu = true;
            _isLightTheme = _theme.ActiveBaseTheme == ThemeVariant.Light;
            Themeing = localizer["THEMING"];
            AvailableColors = _theme.ColorThemes;
            _dialogManager = dialogManager;
            _noticeService.Subscribe(EventNameConst.CustomAddThemColorDataEvent, CustomAddThemColor);
            _custom = (CustomThemColorModel)_service.GetKeyedService<ModelBase>("CustomThemColor")!;
            InitBackgroundStyle();
            if (AppInitData.Config != null)
            {
                _currentLangue = LanguageItems.First(x => AppInitData.Config.Language == x.Value);
                BackgroundStyle = AvailableBackgroundStyles!.FirstOrDefault(x => x.Value == AppInitData.Config.BackgroundStyle);
                _backgroundAnimations = AppInitData.Config.BackgroundAnimations;
            }
            else
            {
                _currentLangue = LanguageItems.First();
            }

            // 初始化 ComfyUI 配置
            if (AppInitData.Config != null)
            {
                ComfyApiUrl = AppInitData.Config.ComfyApiUrl;
                ComfyOutputDir = AppInitData.Config.ComfyOutputDir;
            }

            // T0.1：从 settings.db 读当前工作空间目录
            try
            {
                var appCfg = PromptCraft.Data.ConfigRepository.LoadFromDb();
                WorkspaceDir = appCfg?.WorkspaceDir ?? string.Empty;
                if (string.IsNullOrEmpty(WorkspaceDir))
                    WorkspaceDir = PromptCraft.Service.WorkspaceService.DefaultRoot;

                // T0.4：翻译引擎 + 百度配置
                TranslateEngine = string.IsNullOrEmpty(appCfg?.TranslateEngine) ? "openai" : appCfg.TranslateEngine;
                BaiduAppId = appCfg?.BaiduAppId ?? string.Empty;
                BaiduAppKey = appCfg?.BaiduAppKey ?? string.Empty;
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn(Localizer.Instance?["LoadWorkspaceDirFailed"] ?? "", "Setting", ex);
            }

            // T0.7：加载 AI 提供商列表
            _ = LoadProvidersAsync();

            // 初始化日志级别（与当前生效的 MinLevel 保持一致）。
            // 注意：先置初始化标志，避免 OnSelectedLogLevelChanged 把默认值写回数据库，
            // 覆盖用户上次保存的级别。
            _initializingLogLevel = true;
            var current = log.MinLevel;
            SelectedLogLevel = AvailableLogLevels.FirstOrDefault(x => x.Value == current)
                ?? AvailableLogLevels.FirstOrDefault(x => x.Value == LogLevel.Info)!
                ?? AvailableLogLevels[0];
            _initializingLogLevel = false;

            // 初始化完成，后续 OnXxxChanged 才允许自动落库
            _loadingSettings = false;
        }

        private void InitBackgroundStyle()
        {
            AvailableBackgroundStyles = new AvaloniaList<ListItem<SukiBackgroundStyle>>(Enum.GetValues<SukiBackgroundStyle>().Select(x => new ListItem<SukiBackgroundStyle>(x, _local[x.ToString()], 0)));
            if (AppInitData.Config != null)
                BackgroundStyle = AvailableBackgroundStyles.FirstOrDefault(x => x.Value == AppInitData.Config.BackgroundStyle);
        }

        private void CustomAddThemColor(object? obj)
        {
            if (obj == null || obj is not SukiColorTheme)
            {
                return;
            }
            _addColor = (SukiColorTheme)obj;
        }

        partial void OnIsLightThemeChanged(bool value)
        {
            _theme.ChangeBaseTheme(value ? ThemeVariant.Light : ThemeVariant.Dark);
        }

        public override void OnSystemLangueChanged(object? data)
        {
            if (AppInitData.Config != null && BackgroundStyle != null)
                AppInitData.Config.BackgroundStyle = BackgroundStyle.Value;
            Themeing = _local["THEMING"];
            InitBackgroundStyle();
        }

        [RelayCommand]
        private void SwitchToColorTheme(SukiColorTheme colorTheme)
        {
            _theme.ChangeColorTheme(colorTheme);
        }

        [RelayCommand]
        private async Task ShowCreateCustomColor()
        {
            var result = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost()
            {
                IconPreset = null,
                //还是会有问题，再次打开时这里会重新绘制页面，导致这里报错
                Content = _custom,//_models.FirstOrDefault(x => x.DisplayName == "CUSTOMTHEMCOLOR"), 
                ActionButtonsPreset = SukiMessageBoxButtons.OK,
                Width = 700,
                ActionButtonsSource = new AvaloniaList<Avalonia.Controls.Button>() { SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.OK, _local["Create"]) }
            },
            new SukiMessageBoxOptions()
            {
                Title = _local["CeateCustomerColorTitle"]
            });
            _custom.Dispose();
            if (result != null && _addColor != null)
            {
                _theme.AddColorTheme(_addColor);

                AppInitData.Config ??= new();
                AppInitData.Config.AddedThemeColors ??= new();
                AppInitData.Config.AddedThemeColors.Add(new AddThemeModel(_addColor));
            }
            _custom = (CustomThemColorModel)_service.GetKeyedService<ModelBase>("CustomThemColor")!;
        }


        partial void OnBackgroundStyleChanged(ListItem<SukiBackgroundStyle>? value) =>
            BackgroundStyleChanged?.Invoke(value?.Value ?? SukiBackgroundStyle.Flat);
        partial void OnBackgroundAnimationsChanged(bool value) =>
            BackgroundAnimationsChanged?.Invoke(value);
        partial void OnBackgroundTransitionsChanged(bool value) =>
            BackgroundTransitionsChanged?.Invoke(value);
        partial void OnCurrentLangueChanged(ListItem<LanguageEnum> value)
        {
            _local.CurrentCulture = _local.AvailableCultures.First(x => x.Name == value.Value.ToString().Replace('_', '-'));
            _noticeService.Publish(EventNameConst.SystemLangueChageEvent, _local.CurrentCulture);
            _log.Info(string.Format(_local["SwitchLanguageLog"], value.Value), "Setting");
        }

        /// <summary>日志输出级别变更：立即生效并持久化（仅用户主动修改时触发；构造初始化阶段跳过）。</summary>
        partial void OnSelectedLogLevelChanged(ListItem<LogLevel> value)
        {
            if (value == null || _initializingLogLevel) return;
            // 立即作用于日志服务（决定后续日志是否输出）
            _log.MinLevel = value.Value;
            // 持久化到配置（内存 + DB）
            if (AppInitData.Config != null)
            {
                AppInitData.Config.LogLevel = (int)value.Value;
                AppInitData.SaveConfig();
            }
            _log.Info(string.Format(_local["LogLevelChangedLog"], value.Value), "Setting");
        }

        [RelayCommand]
        private async Task TestComfyConnectionAsync()
        {
            ComfyConnectionStatus = _local["Testing"];
            ComfyIsConnected = false;
            _log.Info(_local["TestConnectionStart"], "Setting");
            try
            {
                var client = new ComfyUIClient(new ComfySettings { ComfyApiUrl = ComfyApiUrl });
                var ok = await client.TestConnectionAsync();
                ComfyIsConnected = ok;
                ComfyConnectionStatus = ok ? _local["ConnectedSuccess"] : _local["ConnectedFailed"];
                _log.Info(string.Format(_local["TestResultLog"], ok ? _local["Success"] : _local["Failed"], ComfyApiUrl), "Setting");
            }
            catch (Exception ex)
            {
                ComfyConnectionStatus = string.Format(_local["ConnectionError"], ex.Message);
                _log.Error(_local["TestConnectionException"], "Setting", ex);
            }
        }

        [RelayCommand]
        private async Task PickOutputDirAsync()
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                ComfyOutputDir = folder;
                AutoSaveComfy();
            }
        }

        private static async Task<string?> PickFolderAsync()
        {
            try
            {
                var provider = StorageService.GetStorageProvider();
                if (provider == null) return null;
                var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = Localizer.Instance?["SelectFolder"] ?? "",
                    AllowMultiple = false,
                });
                return folders.Count > 0 ? folders[0].Path.LocalPath : null;
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug(Localizer.Instance?["PickFolderFailed"] ?? "", "Setting", ex);
                return null;
            }
        }

        [RelayCommand]
        private void SaveComfySettings()
        {
            if (AppInitData.Config == null) return;
            AppInitData.Config.ComfyApiUrl = ComfyApiUrl;
            AppInitData.Config.ComfyOutputDir = ComfyOutputDir;
            // 写入 DB（主存储）
            AppInitData.SaveConfig();
            ComfyConnectionStatus = _local["SettingsSaved"];
            _log.Info(_local["ComfySettingsSaved"], "Setting");
        }

        // ---- T0.1 工作空间目录 ----

        [RelayCommand]
        private async Task PickWorkspaceDirAsync()
        {
            var folder = await PickFolderAsync();
            if (folder != null)
                WorkspaceDir = folder;
        }

        [RelayCommand]
        private void SaveWorkspaceDir()
        {
            try
            {
                if (!PromptCraft.Service.WorkspaceService.IsValidRootPath(WorkspaceDir, out var err))
                {
                    WorkspaceStatus = "❌ " + err;
                    return;
                }

                var current = PromptCraft.Data.ConfigRepository.LoadFromDb() ?? new PromptCraft.Models.AppConfig();
                current.WorkspaceDir = WorkspaceDir.Trim();
                PromptCraft.Data.ConfigRepository.SaveToDb(current);

                // 立即按新路径建子目录（下次启动才会被 WorkspaceService 加载）
                var ws = new PromptCraft.Service.WorkspaceService();
                ws.InitializeAsync(current.WorkspaceDir).Wait();

                WorkspaceStatus = _local["WorkspaceSaved"];
                _log.Info(string.Format(_local["WorkspaceDirUpdated"], WorkspaceDir), "Setting");
            }
            catch (Exception ex)
            {
                WorkspaceStatus = _local["SaveFailedBadge"];
                LogService.Instance.Error(Localizer.Instance?["SaveWorkspaceDirFailed"] ?? "", "Setting", ex);
            }
        }

        // ==================== T0.4 翻译引擎 ====================

        [RelayCommand]
        private void SaveTranslateEngine()
        {
            try
            {
                var current = PromptCraft.Data.ConfigRepository.LoadFromDb() ?? new PromptCraft.Models.AppConfig();
                current.TranslateEngine = TranslateEngine?.Trim().ToLowerInvariant() == "baidu" ? "baidu" : "openai";
                current.BaiduAppId = BaiduAppId?.Trim() ?? string.Empty;
                current.BaiduAppKey = BaiduAppKey?.Trim() ?? string.Empty;
                PromptCraft.Data.ConfigRepository.SaveToDb(current);
                TranslateStatus = _local["SaveSuccessBadge"];
                _log.Info(string.Format(_local["TranslateEngineSwitched"], current.TranslateEngine), "Setting");
            }
            catch (Exception ex)
            {
                TranslateStatus = _local["SaveFailedBadge"];
                LogService.Instance.Error(Localizer.Instance?["SaveTranslateConfigFailed"] ?? "", "Setting", ex);
            }
        }

        // ==================== T0.7 AI 提供商 ====================

        private IProviderService ProviderService => _service.GetRequiredService<IProviderService>();

        private async Task LoadProvidersAsync()
        {
            try
            {
                // 先修正历史脏数据（扩写/反推默认全局至多一个），再加载列表，
                // 保证编辑回显的「默认」与全局实际生效的默认一致
                await ProviderService.NormalizeGlobalDefaultsAsync(default);
                var all = await ProviderService.GetAllAsync(default);
                Providers.Clear();
                foreach (var p in all) Providers.Add(p);
            }
            catch (Exception ex)
            {
                _log.Error(_local["LoadProvidersFailed"], "Setting", ex);
            }
        }

        [RelayCommand]
        private void NewProvider()
        {
            _editingProviderId = null;
            SelectedProvider = null;
            ProviderEditName = string.Empty;
            ProviderEditBaseUrl = string.Empty;
            ProviderEditApiKey = string.Empty;
            ProviderEditEnabled = true;
            EditableRows.Clear();
            AvailableModelsPool.Clear();
            ProviderTestStatus = string.Empty;
            IsEditingProvider = false;
        }

        [RelayCommand]
        private void StartNewProvider()
        {
            _editingProviderId = null;
            SelectedProvider = null;
            ProviderEditName = string.Empty;
            ProviderEditBaseUrl = string.Empty;
            ProviderEditApiKey = string.Empty;
            ProviderEditEnabled = true;
            EditableRows.Clear();
            IsEditingProvider = true;
        }

        [RelayCommand]
        private async Task EditProviderAsync(ProviderConfig p)
        {
            // 重新从 DB 拉一次，确保 Models 被 Include（列表项里的 Models 可能因 EF 缓存为 null）
            var fresh = await ProviderService.GetByIdAsync(p.Id, default);
            if (fresh == null) return;
            p = fresh;

            _editingProviderId = p.Id;
            SelectedProvider = p;
            ProviderEditName = p.Name;
            ProviderEditBaseUrl = p.BaseUrl;
            ProviderEditApiKey = p.ApiKey; // 回显已存 key，眼睛开关控制遮罩
            ProviderEditEnabled = p.Enabled;
            IsEditingProvider = true;
            // 先把已保存的模型名塞进下拉池，再渲染 EditableRows，避免 ComboBox 选中项匹配不到候选项
            AvailableModelsPool.Clear();
            foreach (var m in p.Models)
            {
                if (!string.IsNullOrWhiteSpace(m.ModelName) && !AvailableModelsPool.Contains(m.ModelName))
                    AvailableModelsPool.Add(m.ModelName);
            }
            EditableRows.Clear();
            foreach (var m in p.Models)
            {
                var row = new ProviderModelRow
                {
                    ModelName = m.ModelName,
                    UseForExpand = m.UseForExpand,
                    UseForReverse = m.UseForReverse,
                    IsDefaultExpand = m.IsDefaultExpand,
                    IsDefaultReverse = m.IsDefaultReverse,
                };
                row.SetOwner(() => EditableRows);
                EditableRows.Add(row);
            }
            ProviderTestStatus = string.Empty;
        }

        [RelayCommand]
        private void AddModelRow()
        {
            var row = new ProviderModelRow { UseForExpand = true, UseForReverse = true };
            // 默认标记由用户显式勾选：「扩写默认/反推默认」全应用全局唯一，不自动设置，允许一个没有
            row.SetOwner(() => EditableRows);
            EditableRows.Add(row);
        }

        [RelayCommand]
        private void RemoveModelRow(ProviderModelRow row)
        {
            EditableRows.Remove(row);
        }

        [RelayCommand]
        private async Task SaveProviderAsync()
        {
            try
            {
                var provider = new ProviderConfig
                {
                    Id = _editingProviderId ?? Guid.NewGuid().ToString("N"),
                    Name = ProviderEditName.Trim(),
                    BaseUrl = ProviderEditBaseUrl.Trim().TrimEnd('/'),
                    ApiKey = ProviderEditApiKey.Trim(),
                    Enabled = ProviderEditEnabled,
                };
                foreach (var row in EditableRows)
                {
                    if (string.IsNullOrWhiteSpace(row.ModelName)) continue;
                    provider.Models.Add(new ProviderModel
                    {
                        ModelName = row.ModelName,
                        UseForExpand = row.UseForExpand,
                        UseForReverse = row.UseForReverse,
                        IsDefaultExpand = row.IsDefaultExpand,
                        IsDefaultReverse = row.IsDefaultReverse,
                    });
                }

                await ProviderService.SaveAsync(provider, default);
                _log.Info(string.Format(_local["ProviderSavedLog"], provider.Name, provider.Models.Count), "Setting");
                await LoadProvidersAsync();
                NewProvider();
                ProviderTestStatus = _local["SaveSuccessBadge"];
                // 通知扩写/反推等页面刷新提供商列表（它们只在构造时加载一次）
                _noticeService.Publish(EventNameConst.ProviderChangedEvent, null);
            }
            catch (Exception ex)
            {
                ProviderTestStatus = string.Format(_local["SaveFailedDetail"], ex.Message);
                _log.Error(_local["SaveProviderFailed"], "Setting", ex);
            }
        }

        [RelayCommand]
        private async Task DeleteProviderAsync(ProviderConfig p)
        {
            // 二次确认
            var confirm = await SukiMessageBox.ShowDialog(
                new SukiMessageBoxHost
                {
                    Content = new TextBlock
                    {
                        Text = string.Format(_local["ConfirmDeleteProvider"], p.Name),
                        Margin = new Thickness(4),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
                },
                new SukiMessageBoxOptions { Title = _local["ConfirmDelete"], MinWidth = 360 });
            if (!(confirm is SukiMessageBoxResult r && r.Equals(SukiMessageBoxResult.OK))) return;

            try
            {
                await ProviderService.DeleteAsync(p.Id, default);
                _log.Info(string.Format(_local["ProviderDeletedLog"], p.Name), "Setting");
                await LoadProvidersAsync();
                NewProvider();
                // 通知扩写/反推等页面刷新提供商列表
                _noticeService.Publish(EventNameConst.ProviderChangedEvent, null);
            }
            catch (Exception ex)
            {
                _log.Error(_local["DeleteProviderFailed"], "Setting", ex);
            }
        }

        /// <summary>拉取模型列表：只填充下拉池，不自动加行（用户自行挑选后点「添加模型」）。</summary>
        [RelayCommand]
        private async Task FetchModelsAsync()
        {
            try
            {
                ProviderTestStatus = _local["FetchingModels"];
                var probe = new ProviderConfig
                {
                    BaseUrl = ProviderEditBaseUrl.Trim().TrimEnd('/'),
                    ApiKey = ProviderEditApiKey.Trim(),
                };
                var models = await ProviderService.TestConnectionAsync(probe, default);
                AvailableModelsPool.Clear();
                foreach (var m in models) AvailableModelsPool.Add(m.Id);
                ProviderTestStatus = string.Format(_local["ModelsFetched"], models.Count);
                _log.Info(string.Format(_local["FetchModelsSuccess"], ProviderEditBaseUrl, models.Count), "Setting");
            }
            catch (Exception ex)
            {
                ProviderTestStatus = string.Format(_local["ErrorBadge"], ex.Message);
                _log.Error(_local["FetchModelsFailed"], "Setting", ex);
            }
        }
    }

    /// <summary>模型列表编辑行（绑定到设置页表格）。</summary>
    public partial class ProviderModelRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _modelName = string.Empty;
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _useForExpand;
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _useForReverse;
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isDefaultExpand;
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isDefaultReverse;

        /// <summary>所属行集合访问器：勾选默认时互斥清除其他行，保证表单内同一角色默认单选（不依赖 RadioButton GroupName 的跨行分组）。</summary>
        private Func<ObservableCollection<ProviderModelRow>>? _owner;
        public void SetOwner(Func<ObservableCollection<ProviderModelRow>> owner) => _owner = owner;

        // 取消勾选扩写能力时，自动清掉扩写默认（不能对一个不参与扩写的模型设默认）
        partial void OnUseForExpandChanged(bool value)
        {
            if (!value && IsDefaultExpand) IsDefaultExpand = false;
        }
        partial void OnUseForReverseChanged(bool value)
        {
            if (!value && IsDefaultReverse) IsDefaultReverse = false;
        }

        // 反向联动：勾选默认扩写时，自动把扩写能力勾上，并清除其他行的扩写默认；反推同理
        partial void OnIsDefaultExpandChanged(bool value)
        {
            if (value)
            {
                if (!UseForExpand) UseForExpand = true;
                ClearOtherRowDefaults(isExpand: true);
            }
        }
        partial void OnIsDefaultReverseChanged(bool value)
        {
            if (value)
            {
                if (!UseForReverse) UseForReverse = true;
                ClearOtherRowDefaults(isExpand: false);
            }
        }

        /// <summary>把同一表单里其他行的同角色默认清掉（改为 false 不会再触发递归清除）。</summary>
        private void ClearOtherRowDefaults(bool isExpand)
        {
            var owner = _owner;
            if (owner == null) return;
            foreach (var row in owner())
            {
                if (ReferenceEquals(row, this)) continue;
                if (isExpand)
                {
                    if (row.IsDefaultExpand) row.IsDefaultExpand = false;
                }
                else
                {
                    if (row.IsDefaultReverse) row.IsDefaultReverse = false;
                }
            }
        }
    }
}
