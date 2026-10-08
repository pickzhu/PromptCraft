using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Common;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference.Novel;
using PromptCraft.Service.AssetGen;
using Ke.Bee.Localization.Localizer.Abstractions;
using Avalonia.Media.Imaging;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 小说 → MiniMaxH3 / Seedance 提示词流水线页。
/// 7 阶段多阶段人工确认：每个阶段 ☑ 开关默认全开，执行后展示可编辑文本 → 用户检查修改 → 确认后自动进下一步；
/// 支持重跑本阶段 / 返回上一步。输出格式下拉含 Seedance 2.0。全部参数 pm_novel_* 记忆。
/// </summary>
public partial class NovelToPromptViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private readonly IServiceProvider _services;
    private IExpandSettingsStore? _settings;
    private bool _settingsLoaded;
    private INovelToPromptService? _svc;

    // 参数记忆 key
    private const string KeyNovelText = "pm_novel_text";
    private const string KeyEnableConcept = "pm_novel_enable_concept";
    private const string KeyEnableCharacters = "pm_novel_enable_characters";
    private const string KeyEnableWorldbuilding = "pm_novel_enable_worldbuilding";
    private const string KeyEnableTreatment = "pm_novel_enable_treatment";
    private const string KeyEnableAssetScan = "pm_novel_enable_assetscan";
    private const string KeyEnableShotPlanning = "pm_novel_enable_shotplanning";
    private const string KeyEnablePrompts = "pm_novel_enable_prompts";
    private const string KeyOutputFormat = "pm_novel_output_format";
    private const string KeyGranularity = "pm_novel_granularity";
    private const string KeyReviewMode = "pm_novel_review_mode";
    private const string KeyOutputLanguage = "pm_novel_output_lang";
    private const string KeyNoRefT2VA = "pm_novel_no_ref_t2va";
    private const string KeyAssetLlmClassify = "pm_novel_asset_llm_classify";
    private const string KeyKbEnabled = "pm_novel_kb_enabled";
    private const string KeyKbPath = "pm_novel_kb_path";
    private const string KeyAssetDir = "pm_novel_asset_dir";
    private const string KeyGapPrompt = "pm_novel_gap_prompt";
    private const string KeyImageBatch = "pm_novel_gap_image_batch";
    private const string KeyVoiceBatch = "pm_novel_gap_voice_batch";
    private const string KeyAspectRatio = "pm_novel_aspect_ratio";
    private const string KeyDefaultShotSeconds = "pm_novel_default_shot_seconds";
    private const string KeyProvider = "pm_novel_provider";
    private const string KeyModel = "pm_novel_model";

    // 阶段开关
    [ObservableProperty] private bool _enableConcept = true;
    [ObservableProperty] private bool _enableCharacters = true;
    [ObservableProperty] private bool _enableWorldbuilding = true;
    [ObservableProperty] private bool _enableTreatment = true;
    [ObservableProperty] private bool _enableAssetScan = true;
    [ObservableProperty] private bool _enableShotPlanning = true;
    [ObservableProperty] private bool _enablePrompts = true;

    // 格式 / 粒度 / 审查 / 资产 / 知识库
    [ObservableProperty] private int _outputFormatIndex; // 0 中文直投 / 1 Minimax六段式通用 / 2 Seedance / 3 连续剧情（导演台）
    [ObservableProperty] private int _granularityIndex;   // 0 逐镜头 / 1 逐场合并
    [ObservableProperty] private int _reviewModeIndex;    // 0 一次全部 / 1 逐场滚动
    [ObservableProperty] private int _outputLanguageIndex; // 0 中文 / 1 English（强制所有输出使用所选语言）
    /// <summary>无参考图时改用三字段 T2VA（仅六段式格式生效，默认关闭）。</summary>
    [ObservableProperty] private bool _enableNoRefT2VA;
    [ObservableProperty] private bool _assetLlmClassify;
    [ObservableProperty] private bool _knowledgeBaseEnabled;
    [ObservableProperty] private string _knowledgeBasePath = "";
    [ObservableProperty] private string _assetDirectory = "";
    [ObservableProperty] private bool _enableGapPrompt = true;
    [ObservableProperty] private int _imageBatchSize = 5;
    [ObservableProperty] private int _voiceBatchSize = 10;
    [ObservableProperty] private string _aspectRatio = "16:9";
    [ObservableProperty] private double _defaultShotSeconds = 6;
    [ObservableProperty] private ProviderConfig? _selectedProvider;
    [ObservableProperty] private ProviderModel? _selectedModel;

    // 小说原文
    [ObservableProperty] private string _novelText = "";

    // 流水线状态
    [ObservableProperty] private bool _isRunning;
    /// <summary>阶段 LLM 正在执行中（执行间隙可编辑资产/知识库路径等输入）。</summary>
    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private string _currentStageLabel = "";
    [ObservableProperty] private string _editableText = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _hasEditable = true;
    [ObservableProperty] private bool _isFinished;
    // 缺口提示词分批生成状态（S5 内，不持久化）
    [ObservableProperty] private int _gapOffset;
    [ObservableProperty] private int _gapTotal;
    [ObservableProperty] private bool _isGeneratingGaps;

    public AvaloniaList<StageItem> StageItems { get; } = new();
    /// <summary>阶段行展示项：Status 文本状态；IsRunning 时显示进度条，Progress 为 null 时不确定进度，ProgressText 为进度文案。</summary>
    public sealed record StageItem(NovelStage Stage, string Label, string Status, double? Progress = null, string? ProgressText = null, bool IsRunning = false)
    {
        public bool IsIndeterminate => Progress == null;
    }

    // 格式 / 粒度 / 审查下拉项（本地化显示名）
    public IReadOnlyList<string> OutputFormatItems =>
        new[] { "NovelFormat_ChineseDirect", "NovelFormat_H3FullReference", "NovelFormat_Seedance", "NovelFormat_H3DirectorStory" }
            .Select(k => _local[k] ?? k).ToList();
    public IReadOnlyList<string> GranularityItems =>
        new[] { "NovelGranularity_PerShot", "NovelGranularity_PerSceneMerged" }
            .Select(k => _local[k] ?? k).ToList();
    public IReadOnlyList<string> ReviewModeItems =>
        new[] { "NovelReview_AllAtOnce", "NovelReview_SceneByScene" }
            .Select(k => _local[k] ?? k).ToList();
    /// <summary>输出语言（只提供中文/English）。</summary>
    public IReadOnlyList<string> OutputLanguageItems =>
        new[] { "NovelLang_Chinese", "NovelLang_English" }
            .Select(k => _local[k] ?? k).ToList();

    public string Title => _local["NovelTitle"];

    // ---- 服务商 / 模型选择（复用 IProviderService）----
    public AvaloniaList<ProviderConfig> Providers { get; } = new();
    public AvaloniaList<ProviderModel> FilteredModels { get; } = new();
    public string NoModelsText => string.Format(_local["NoModelsText"], SelectedProvider?.Name ?? "");
    public bool HasExpandModels => FilteredModels.Count > 0;

    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyProvider, value?.Id ?? "");
        RefreshFilteredModels();
        OnPropertyChanged(nameof(HasExpandModels));
        OnPropertyChanged(nameof(NoModelsText));
        if (SelectedModel == null && FilteredModels.Count > 0) SelectedModel = FilteredModels[0];
    }
    partial void OnSelectedModelChanged(ProviderModel? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyModel, value?.ModelName ?? "");
    }

    private void RefreshFilteredModels()
    {
        FilteredModels.Clear();
        if (SelectedProvider == null) return;
        foreach (var m in SelectedProvider.Models) FilteredModels.Add(m);
    }

    public void GoConfigureProvider() => _noticeService.Publish(EventNameConst.SystemNavigatePageEvent, typeof(SettingModel));
    public IRelayCommand GoSettingsCommand => new RelayCommand(GoConfigureProvider);

    private async Task ReloadProvidersAsync()
    {
        try
        {
            var all = await _services.GetRequiredService<IProviderService>().GetAllAsync(default);
            Providers.Clear();
            foreach (var p in all) Providers.Add(p);

            var provId = _settings?.Get(KeyProvider);
            var modelName = _settings?.Get(KeyModel);
            ProviderConfig? selProv = null;
            ProviderModel? selModel = null;
            if (!string.IsNullOrEmpty(provId) && !string.IsNullOrEmpty(modelName))
            {
                selProv = Providers.FirstOrDefault(p => p.Id == provId);
                selModel = selProv?.Models.FirstOrDefault(m => m.ModelName == modelName);
                if (selModel == null) selProv = null;
            }
            if (selProv == null || selModel == null)
            {
                foreach (var p in Providers)
                {
                    var m = p.Models.FirstOrDefault(x => x.IsDefaultExpand);
                    if (m != null) { selProv = p; selModel = m; break; }
                }
            }
            SelectedProvider = selProv ?? Providers.FirstOrDefault();
            SelectedModel = selModel ?? SelectedProvider?.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["LoadProvidersFailedLog"], ex.Message), "Novel");
        }
    }

    /// <summary>确认并继续：执行中禁用（含历史查看时），未完成且（历史查看中或已有结果）时可用。</summary>
    public bool CanConfirm => !IsFinished && !IsExecuting && (IsViewingHistory || _currentResult != null);
    public bool CanGoBack
    {
        get
        {
            if (IsExecuting) return false;
            if (IsViewingHistory && _viewingHistoryStage is { } hs)
            {
                var idx = IndexOfStage(hs);
                return idx > 0;
            }
            return _currentStageIndex > 0;
        }
    }
    /// <summary>重跑本阶段：执行中禁用（含历史查看时），历史查看中或已有结果时可用。</summary>
    public bool CanRerun => !IsExecuting && (IsViewingHistory || _currentResult != null);
    /// <summary>「重新开始」按钮：存在已确认/已跳过进度且未在运行时可用。</summary>
    public bool CanRestartPipeline => !IsRunning
        && StageItems.Any(x => x.Status == StageStatusConfirmed || x.Status == StageStatusSkipped);
    public bool HasEditableText => !string.IsNullOrWhiteSpace(EditableText);
    /// <summary>正在查看已确认阶段的历史结果（双击阶段列表进入，不改变流水线推进状态）。</summary>
    public bool IsViewingHistory => _viewingHistoryStage != null;

    // ---- 资产提示词分批（S5 资产扫描阶段展示）----
    /// <summary>缺口批次相关 UI 仅属于资产扫描阶段上下文：正在查看其他阶段历史时不显示（避免在别的阶段输出区出现上一批/下一批等按钮）。</summary>
    private bool IsGapViewContextActive => _viewingHistoryStage is null or NovelStage.AssetScan;
    /// <summary>当前结果处于资产扫描阶段且有缺失资产（图像或音色）。</summary>
    public bool HasMissingAssets =>
        _currentResult?.AssetScan is { } s && (s.Gaps.Count > 0 || s.VoiceGaps.Count > 0);
    /// <summary>「生成提示词」按钮可见性：资产扫描输出框下始终显示（有缺失资产时；生成期间保持可见仅禁用并显示 loading）。</summary>
    public bool ShowGenerateGapBatchButton =>
        IsGapViewContextActive && _currentResult?.Stage == NovelStage.AssetScan && EnableGapPrompt && HasMissingAssets;
    /// <summary>「生成提示词」可用性：未生成完时可用（未开始 = 第一批；已生成部分 = 继续下一批；全部生成完 = 禁用，改用「重新生成」）。</summary>
    public bool CanGenerateGapBatch =>
        ShowGenerateGapBatchButton && !IsExecuting && !IsGeneratingGaps
        && (GapTotal == 0 || GapOffset < GapTotal);
    /// <summary>「重新生成提示词」：已生成过批次时可用（清空已生成批次后从头重新生成）。</summary>
    public bool CanRegenerateGapPrompts =>
        IsGapViewContextActive && _currentResult?.Stage == NovelStage.AssetScan && !IsExecuting && !IsGeneratingGaps && EnableGapPrompt
        && GapTotal > 0;
    /// <summary>「上一批」：查看上一批已生成的提示词（仅资产扫描阶段上下文可见）。</summary>
    public bool CanGoPrevGapBatch => IsGapViewContextActive && _gapBatchIndex > 0;
    /// <summary>「下一批」：查看下一批已生成的提示词（仅资产扫描阶段上下文可见）。</summary>
    public bool CanGoNextGapBatch => IsGapViewContextActive && _gapBatchIndex >= 0 && _gapBatchIndex < _gapBatchSections.Count - 1;
    /// <summary>「显示资产扫描结果」：当前输出区显示的是批次提示词时可用（仅资产扫描阶段上下文可见）。</summary>
    public bool CanShowScanResult => IsGapViewContextActive && _currentResult?.AssetScan != null && !_showingScanResult;
    /// <summary>存在缺失资产（批次进度可见，仅资产扫描阶段上下文）。</summary>
    public bool HasGapBatches => IsGapViewContextActive && _currentResult?.AssetScan != null && GapTotal > 0;
    public string GapProgressText => string.Format(_local["NovelGapProgress"] ?? "{0}/{1}", Math.Min(GapOffset, GapTotal), GapTotal);

    // ---- 资产生图（v2 #4：半自动模板制，环境自适应 + fail-fast；生成 → pending → 用户验收）----
    /// <summary>待验收的生成资产（确认移入资产目录 / 删除）。</summary>
    public AvaloniaList<PendingAssetItem> PendingAssets { get; } = new();
    [ObservableProperty] private PendingAssetItem? _selectedPendingAsset;
    /// <summary>资产生图执行中（生成/提交阶段）。</summary>
    [ObservableProperty] private bool _isGeneratingAssets;
    /// <summary>资产生图过程状态（探测/提交/生成/验收提示）。</summary>
    [ObservableProperty] private string _assetGenStatus = "";

    /// <summary>「自动生成资产图」可见性：有缺失资产时可见（资产生图是缺口提示词的直接执行通道）。
    /// v2.1 暂隐藏：ComfyUI /object_info 的 COMBO 候选解析存在 bug（NodeField.Options 只存了选项字典、候选列表被丢弃），
    /// 导致有模型也误报「没有任何可用的 checkpoint 模型」。修复解析后恢复此表达式即可。</summary>
    public bool ShowGenerateAssetImagesButton => false;
    /// <summary>「自动生成资产图」可用性：未在执行/生成提示词/生成资产图时可用；有未验收 pending 时禁用（先验收再继续）。</summary>
    public bool CanGenerateAssetImages =>
        ShowGenerateAssetImagesButton && !IsExecuting && !IsGeneratingGaps && !IsGeneratingAssets && PendingAssets.Count == 0;
    public bool HasPendingAssets => IsGapViewContextActive && PendingAssets.Count > 0;
    public bool HasSelectedPendingAsset => SelectedPendingAsset != null;

    partial void OnIsGeneratingAssetsChanged(bool value) => RefreshGapButtons();
    partial void OnSelectedPendingAssetChanged(PendingAssetItem? value) => OnPropertyChanged(nameof(HasSelectedPendingAsset));

    private void RefreshAssetGenUi()
    {
        OnPropertyChanged(nameof(CanGenerateAssetImages));
        OnPropertyChanged(nameof(ShowGenerateAssetImagesButton));
        OnPropertyChanged(nameof(HasPendingAssets));
    }

    private void RefreshGapButtons()
    {
        OnPropertyChanged(nameof(CanGenerateGapBatch));
        OnPropertyChanged(nameof(ShowGenerateGapBatchButton));
        OnPropertyChanged(nameof(CanRegenerateGapPrompts));
        OnPropertyChanged(nameof(CanGoPrevGapBatch));
        OnPropertyChanged(nameof(CanGoNextGapBatch));
        OnPropertyChanged(nameof(CanShowScanResult));
        OnPropertyChanged(nameof(HasGapBatches));
        OnPropertyChanged(nameof(HasMissingAssets));
        OnPropertyChanged(nameof(GapProgressText));
        RefreshAssetGenUi();
    }

    partial void OnIsGeneratingGapsChanged(bool value) => RefreshGapButtons();
    partial void OnGapOffsetChanged(int value) => RefreshGapButtons();
    partial void OnGapTotalChanged(int value) => RefreshGapButtons();

    partial void OnEditableTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasEditableText));
        // 资产扫描阶段且当前显示扫描结果时，同步维护“阶段规范文本”（切到批次视图后仍保留用户对扫描文本的修改）
        if (!IsViewingHistory && _currentResult?.Stage == NovelStage.AssetScan && _showingScanResult)
            _assetScanText = value ?? "";
    }
    partial void OnIsFinishedChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanRestartPipeline));
    partial void OnIsExecutingChanged(bool value)
    {
        // 执行中统一禁用「确认并继续/重跑本阶段/返回上一步」（含历史查看时），并刷新缺口提示词按钮状态
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanGoBack));
        RefreshGapButtons();
    }
    partial void OnOutputFormatIndexChanged(int value) { if (_settingsLoaded) _settings?.Set(KeyOutputFormat, value.ToString()); }
    partial void OnGranularityIndexChanged(int value) { if (_settingsLoaded) _settings?.Set(KeyGranularity, value.ToString()); }
    partial void OnReviewModeIndexChanged(int value) { if (_settingsLoaded) _settings?.Set(KeyReviewMode, value.ToString()); }
    partial void OnOutputLanguageIndexChanged(int value) { if (_settingsLoaded) _settings?.Set(KeyOutputLanguage, value.ToString()); }
    partial void OnEnableNoRefT2VAChanged(bool value) { if (_settingsLoaded) _settings?.Set(KeyNoRefT2VA, value ? "1" : "0"); }
    partial void OnAssetLlmClassifyChanged(bool value) { if (_settingsLoaded) _settings?.Set(KeyAssetLlmClassify, value ? "1" : "0"); }
    partial void OnKnowledgeBaseEnabledChanged(bool value) { if (_settingsLoaded) _settings?.Set(KeyKbEnabled, value ? "1" : "0"); }
    partial void OnKnowledgeBasePathChanged(string value) { if (_settingsLoaded) _settings?.Set(KeyKbPath, value ?? ""); }
    partial void OnAssetDirectoryChanged(string value) { if (_settingsLoaded) _settings?.Set(KeyAssetDir, value ?? ""); }
    partial void OnEnableGapPromptChanged(bool value)
    {
        if (_settingsLoaded) _settings?.Set(KeyGapPrompt, value ? "1" : "0");
        RefreshGapButtons();
    }
    partial void OnImageBatchSizeChanged(int value) { if (_settingsLoaded && value > 0) _settings?.Set(KeyImageBatch, value.ToString()); }
    partial void OnVoiceBatchSizeChanged(int value) { if (_settingsLoaded && value > 0) _settings?.Set(KeyVoiceBatch, value.ToString()); }
    partial void OnAspectRatioChanged(string value) { if (_settingsLoaded) _settings?.Set(KeyAspectRatio, value ?? ""); }
    partial void OnDefaultShotSecondsChanged(double value) { if (_settingsLoaded) _settings?.Set(KeyDefaultShotSeconds, value.ToString("0.#")); }

    partial void OnNovelTextChanged(string value)
    {
        if (_settingsLoaded) _settings?.Set(KeyNovelText, value ?? "");
        // 已恢复的断点进度随小说文本变更而失效
        if (_restoredFromCache
            && !string.Equals(HashText(value), _restoredNovelHash, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateRestoredPipeline();
        }
    }

    private void StageToggleChanged()
    {
        if (!_settingsLoaded) return;
        _settings?.Set(KeyEnableConcept, EnableConcept ? "1" : "0");
        _settings?.Set(KeyEnableCharacters, EnableCharacters ? "1" : "0");
        _settings?.Set(KeyEnableWorldbuilding, EnableWorldbuilding ? "1" : "0");
        _settings?.Set(KeyEnableTreatment, EnableTreatment ? "1" : "0");
        _settings?.Set(KeyEnableAssetScan, EnableAssetScan ? "1" : "0");
        _settings?.Set(KeyEnableShotPlanning, EnableShotPlanning ? "1" : "0");
        _settings?.Set(KeyEnablePrompts, EnablePrompts ? "1" : "0");
    }
    partial void OnEnableConceptChanged(bool v) => StageToggleChanged();
    partial void OnEnableCharactersChanged(bool v) => StageToggleChanged();
    partial void OnEnableWorldbuildingChanged(bool v) => StageToggleChanged();
    partial void OnEnableTreatmentChanged(bool v) => StageToggleChanged();
    partial void OnEnableAssetScanChanged(bool v) => StageToggleChanged();
    partial void OnEnableShotPlanningChanged(bool v) => StageToggleChanged();
    partial void OnEnablePromptsChanged(bool v) => StageToggleChanged();

    // 流水线运行时内部状态
    private List<NovelStage> _enabledStages = new();
    private int _currentStageIndex;
    private NovelPipelineContext _ctx = new();
    private NovelStageResult? _currentResult;
    private CancellationTokenSource? _cts;
    /// <summary>缺口提示词生成专用取消源（与整条流水线 _cts 隔离，停止生成不影响流水线）。</summary>
    private CancellationTokenSource? _gapCts;
    // 会话代际：每次开始流程递增；旧会话（被停止后取消的异步调用）收尾时若代际已变，说明已被新会话取代，放弃收尾，避免污染新会话状态
    private int _pipelineGen;
    // 阶段确认后的状态快照（回退时回显，不重跑）
    private readonly Dictionary<NovelStage, StageSnapshot> _snapshots = new();
    // 资产提示词批次展示状态
    private readonly List<string> _gapBatchSections = new(); // 已生成的批次提示词文本
    private int _gapBatchIndex = -1;                          // 当前查看的批次索引（-1=尚未生成）
    private bool _showingScanResult = true;                   // 输出区当前显示的是资产扫描结果
    private string _assetScanText = "";                       // 资产扫描阶段规范文本（扫描结果，含用户修改；批次视图切换时保留）
    // 历史查看状态（双击已确认阶段进入，只读回显，不改变流水线推进状态）
    private NovelStage? _viewingHistoryStage;                 // 正在查看历史的阶段（null=未查看）
    private string _savedEditableText = "";                   // 进入历史查看前的输出区内容
    private string _savedStatusText = "";                     // 进入历史查看前的状态提示
    private string _savedStageLabel = "";                     // 进入历史查看前的当前阶段标签

    /// <summary>阶段状态快照：保存确认/执行时的结果与展示状态，供「返回上一步」回显。</summary>
    private sealed class StageSnapshot
    {
        public NovelStageResult? Result;
        public string EditableText = "";
        public int GapOffset;
        public int GapTotal;
        public List<string> GapBatchSections = new();
        public int GapBatchIndex = -1;
        public bool ShowingScanResult = true;
    }

    // ---- 断点续跑（已确认阶段结果持久化，重启后小说未变可恢复）----
    private bool _restoredFromCache;          // 当前已恢复缓存进度（尚未开始跑）
    private string _restoredNovelHash = "";   // 恢复时的小说哈希（用于检测文本变更）
    private static readonly JsonSerializerOptions CacheJsonOpts = new() { WriteIndented = true };

    /// <summary>断点缓存数据（%APPDATA%/PromptCraft/novel_pipeline_cache.json）。</summary>
    private sealed class PipelineCacheData
    {
        public string NovelHash { get; set; } = "";
        public string NovelText { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public List<StageCacheEntry> Stages { get; set; } = new();
        public NovelPipelineContext Context { get; set; } = new();
    }

    /// <summary>缓存中的单个阶段条目（仅已确认/已跳过阶段落盘）。</summary>
    private sealed class StageCacheEntry
    {
        public NovelStage Stage { get; set; }
        public string Status { get; set; } = "";
        public string EditableText { get; set; } = "";
        public AssetScanResult? AssetScan { get; set; }
        public ShotPlanResult? ShotPlan { get; set; }
        public int GapOffset { get; set; }
        public int GapTotal { get; set; }
        public List<string> GapBatchSections { get; set; } = new();
        public int GapBatchIndex { get; set; } = -1;
        public bool ShowingScanResult { get; set; } = true;
    }

    private static string PipelineCachePath =>
        Path.Combine(StorageService.GetAppDataDirectory("PromptCraft"), "novel_pipeline_cache.json");

    private static string HashText(string? text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));

    /// <summary>把当前已确认/已跳过阶段与上下文写入断点缓存（每次确认阶段后调用）。</summary>
    private void SavePipelineCache()
    {
        try
        {
            var data = new PipelineCacheData
            {
                NovelHash = HashText(NovelText),
                NovelText = NovelText ?? "",
                SavedAt = DateTime.Now,
                Context = _ctx,
                Stages = _enabledStages
                    .Where(s =>
                    {
                        var st = StageItems.FirstOrDefault(x => x.Stage == s)?.Status ?? "";
                        return st == StageStatusConfirmed || st == StageStatusSkipped;
                    })
                    .Select(s =>
                    {
                        _snapshots.TryGetValue(s, out var snap);
                        var st = StageItems.FirstOrDefault(x => x.Stage == s)?.Status ?? "";
                        return new StageCacheEntry
                        {
                            Stage = s,
                            Status = st == StageStatusConfirmed ? "Confirmed" : "Skipped",
                            EditableText = snap?.EditableText ?? "",
                            AssetScan = snap?.Result?.AssetScan,
                            ShotPlan = snap?.Result?.ShotPlan,
                            GapOffset = snap?.GapOffset ?? 0,
                            GapTotal = snap?.GapTotal ?? 0,
                            GapBatchSections = snap?.GapBatchSections.ToList() ?? new List<string>(),
                            GapBatchIndex = snap?.GapBatchIndex ?? -1,
                            ShowingScanResult = snap?.ShowingScanResult ?? true,
                        };
                    })
                    .ToList(),
            };
            File.WriteAllText(PipelineCachePath, JsonSerializer.Serialize(data, CacheJsonOpts));
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Debug($"保存小说流水线断点缓存失败：{ex.Message}", "Novel", ex);
        }
    }

    /// <summary>页面加载后尝试恢复：小说文本未变则恢复已确认/已跳过阶段与上下文。</summary>
    private void TryRestorePipelineCache()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(NovelText)) return;
            var path = PipelineCachePath;
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<PipelineCacheData>(File.ReadAllText(path), CacheJsonOpts);
            if (data?.Stages == null || data.Context == null) return;
            if (!string.Equals(data.NovelHash, HashText(NovelText), StringComparison.OrdinalIgnoreCase)) return; // 小说已改变 → 不恢复

            var restoredAny = false;
            foreach (var e in data.Stages)
            {
                if (e.Status != "Confirmed" && e.Status != "Skipped") continue;
                var result = new NovelStageResult { Stage = e.Stage, Text = e.EditableText, AssetScan = e.AssetScan, ShotPlan = e.ShotPlan };
                // 资产扫描阶段：无论旧缓存保存的是什么，一律以「资产扫描结果」为阶段内容并默认显示扫描结果（批次提示词仍可翻页浏览）
                var editableText = e.EditableText;
                var showingScan = e.ShowingScanResult;
                if (e.AssetScan != null && (string.IsNullOrEmpty(editableText) || !showingScan))
                {
                    editableText = e.AssetScan.RawText ?? "";
                    showingScan = true;
                }
                if (e.AssetScan != null) _assetScanText = editableText;
                _snapshots[e.Stage] = new StageSnapshot
                {
                    Result = result,
                    EditableText = editableText,
                    GapOffset = e.GapOffset,
                    GapTotal = e.GapTotal,
                    GapBatchSections = e.GapBatchSections.ToList(),
                    GapBatchIndex = e.GapBatchIndex,
                    ShowingScanResult = showingScan,
                };
                UpdateStatus(e.Stage, e.Status == "Confirmed" ? StageStatusConfirmed : StageStatusSkipped);
                restoredAny = true;
            }
            if (!restoredAny) return;

            _ctx = data.Context;
            _ctx.NovelText = NovelText ?? "";
            _restoredFromCache = true;
            _restoredNovelHash = data.NovelHash;
            // 定位到第一个待执行阶段，使「确认并继续 / 重跑本阶段 / 返回上一步」按需可用，而非全部按钮失效
            _enabledStages = BuildEnabledStages();
            var pendingIdx = -1;
            for (var i = 0; i < _enabledStages.Count; i++)
            {
                var st = StageItems.FirstOrDefault(x => x.Stage == _enabledStages[i])?.Status ?? "";
                if (st != StageStatusConfirmed && st != StageStatusSkipped) { pendingIdx = i; break; }
            }
            if (pendingIdx >= 0)
            {
                _currentStageIndex = pendingIdx;
                CurrentStageLabel = StageLabel(_enabledStages[pendingIdx]);
                // 该待执行阶段若有快照（本次会话内停止后未确认的执行结果）则回显
                if (_snapshots.TryGetValue(_enabledStages[pendingIdx], out var pendSnap))
                    RestoreSnapshot(pendSnap);
            }
            var firstPending = StageItems.FirstOrDefault(x => x.Status != StageStatusConfirmed && x.Status != StageStatusSkipped);
            StatusText = string.Format(_local["NovelCacheRestored"] ?? "已恢复上次进度：已确认阶段可直接查看/修改，开始后将从【{0}】继续；如需从头执行可点击「重新开始」",
                firstPending?.Label ?? StageStatusPending);
            OnPropertyChanged(nameof(CanRestartPipeline));
            ShowToast(_local["NovelCacheRestoredToast"] ?? "已恢复上次流水线进度", "");
            PromptCraft.Service.LogService.Instance.Info($"恢复小说流水线断点缓存：{data.Stages.Count} 个阶段已确认/跳过", "Novel");
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Debug($"恢复小说流水线断点缓存失败：{ex.Message}", "Novel", ex);
        }
    }

    /// <summary>小说文本变更后使恢复的进度失效（回到全部待执行）。</summary>
    private void InvalidateRestoredPipeline()
    {
        _restoredFromCache = false;
        _restoredNovelHash = "";
        _snapshots.Clear();
        _ctx = new NovelPipelineContext();
        _currentResult = null;
        _currentStageIndex = 0;
        _viewingHistoryStage = null;
        _gapBatchSections.Clear();
        _gapBatchIndex = -1;
        _assetScanText = "";
        GapOffset = 0;
        GapTotal = 0;
        foreach (var s in Enum.GetValues<NovelStage>())
            UpdateStatus(s, StageStatusPending);
        if (!IsRunning) StatusText = "";
        OnPropertyChanged(nameof(CanRestartPipeline));
        PromptCraft.Service.LogService.Instance.Info("小说文本已变更，放弃已恢复的流水线进度", "Novel");
    }

    private StageSnapshot BuildSnapshot() => new()
    {
        Result = _currentResult,
        // 资产扫描阶段始终以「扫描结果文本（含用户修改）」为阶段内容落快照，避免把批次提示词误存为阶段输出
        EditableText = _currentResult?.Stage == NovelStage.AssetScan ? (_assetScanText ?? EditableText ?? "") : (EditableText ?? ""),
        GapOffset = GapOffset,
        GapTotal = GapTotal,
        GapBatchSections = _gapBatchSections.ToList(),
        GapBatchIndex = _gapBatchIndex,
        ShowingScanResult = _currentResult?.Stage == NovelStage.AssetScan || _showingScanResult,
    };

    private void RestoreSnapshot(StageSnapshot snap)
    {
        _currentResult = snap.Result;
        EditableText = snap.EditableText ?? "";
        ErrorMessage = "";
        GapOffset = snap.GapOffset;
        GapTotal = snap.GapTotal;
        _gapBatchSections.Clear();
        _gapBatchSections.AddRange(snap.GapBatchSections);
        _gapBatchIndex = snap.GapBatchIndex;
        _showingScanResult = snap.ShowingScanResult;
        RefreshGapButtons();
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(HasEditableText));
    }

    private void RefreshHistoryViewState()
    {
        OnPropertyChanged(nameof(IsViewingHistory));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanGoBack));
        // 历史查看上下文变化 → 刷新缺口批次按钮可见性（查看其他阶段历史时隐藏上一批/下一批等）
        RefreshGapButtons();
    }

    /// <summary>退出历史查看。restoreTextAndStatus=true 时恢复进入前的输出区与状态提示；false 时保留当前输出与提示（阶段执行完成/失败后调用）。</summary>
    private void RestorePipelineViewCore(bool restoreTextAndStatus)
    {
        _viewingHistoryStage = null;
        CurrentStageLabel = _savedStageLabel;
        if (restoreTextAndStatus)
        {
            EditableText = _savedEditableText;
            StatusText = _savedStatusText;
        }
        RefreshHistoryViewState();
    }

    /// <summary>双击阶段列表项：已确认阶段进入历史查看（再次双击退出）；历史查看中双击待确认/待执行阶段则返回当前流程。</summary>
    [RelayCommand]
    private void ShowStageHistory(StageItem? item)
    {
        if (item == null) return;
        var stage = item.Stage;
        // 仅已确认且存在快照的阶段可查看历史
        var confirmed = StageItems.FirstOrDefault(x => x.Stage == stage)?.Status == StageStatusConfirmed;
        // 历史查看状态下：再次双击当前查看阶段 → 退出；双击非已确认阶段（待确认/待执行/当前进度）→ 返回当前流程
        if (_viewingHistoryStage != null)
        {
            if (_viewingHistoryStage == stage)
            {
                RestorePipelineViewCore(restoreTextAndStatus: true);
                return;
            }
            if (!confirmed)
            {
                RestorePipelineViewCore(restoreTextAndStatus: true);
                return;
            }
        }
        if (!confirmed || !_snapshots.TryGetValue(stage, out var snap)) return;
        // 首次进入：保存当前显示状态，回显历史结果（切换查看目标时不覆盖已保存状态）
        if (_viewingHistoryStage == null)
        {
            _savedEditableText = EditableText ?? "";
            _savedStatusText = StatusText ?? "";
            _savedStageLabel = CurrentStageLabel ?? "";
        }
        _viewingHistoryStage = stage;
        EditableText = snap.EditableText;
        StatusText = string.Format(_local["NovelHistoryViewing"] ?? "正在查看已确认阶段【{0}】的历史结果，双击该阶段或点击“恢复流水线”退出", StageLabel(stage));
        RefreshHistoryViewState();
        PromptCraft.Service.LogService.Instance.Info($"查看已确认阶段 {stage} 的历史结果", "Novel");
    }

    [RelayCommand]
    private void RestorePipelineView() => RestorePipelineViewCore(restoreTextAndStatus: true);

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(OutputFormatItems));
        OnPropertyChanged(nameof(GranularityItems));
        OnPropertyChanged(nameof(ReviewModeItems));
        OnPropertyChanged(nameof(OutputLanguageItems));
        RebuildStageLabels();
    }

    public NovelToPromptViewModel(ILocalizer localizer, IBaseNotice baseNotice, IServiceProvider services)
        : base(localizer, baseNotice)
    {
        _displayName = "NOVEL2PROMPT";
        _icon = MaterialIconKind.MovieFilter;
        _index = 42;
        _sideMenu = true;
        _services = services;
        _settings = services.GetRequiredService<IExpandSettingsStore>();
        _svc = services.GetRequiredService<INovelToPromptService>();
        _noticeService.Subscribe(EventNameConst.ProviderChangedEvent, _ => _ = ReloadProvidersAsync());
        RebuildStageLabels();
        _ = LoadAsync();
    }

    private string StageLabel(NovelStage s) => _local[$"NovelStage_{s}"] ?? s.ToString();
    private string StageStatusPending => _local["NovelStatus_Pending"] ?? "待执行";
    private string StageStatusRunning => _local["NovelStatus_Running"] ?? "执行中";
    private string StageStatusReview => _local["NovelStatus_Review"] ?? "待确认";
    private string StageStatusConfirmed => _local["NovelStatus_Confirmed"] ?? "已确认";
    private string StageStatusSkipped => _local["NovelStatus_Skipped"] ?? "已跳过";

    /// <summary>按当前阶段开关构建启用阶段列表（与 StartPipelineAsync 一致）。</summary>
    private List<NovelStage> BuildEnabledStages()
    {
        var list = new List<NovelStage>();
        if (EnableConcept) list.Add(NovelStage.Concept);
        if (EnableCharacters) list.Add(NovelStage.Characters);
        if (EnableWorldbuilding) list.Add(NovelStage.Worldbuilding);
        if (EnableTreatment) list.Add(NovelStage.Treatment);
        if (EnableAssetScan) list.Add(NovelStage.AssetScan);
        if (EnableShotPlanning) list.Add(NovelStage.ShotPlanning);
        if (EnablePrompts) list.Add(NovelStage.Prompts);
        return list;
    }

    private int IndexOfStage(NovelStage stage)
    {
        for (var i = 0; i < _enabledStages.Count; i++)
            if (_enabledStages[i] == stage) return i;
        return -1;
    }

    private void RebuildStageLabels()
    {
        if (StageItems.Count != 7)
        {
            StageItems.Clear();
            foreach (NovelStage s in Enum.GetValues<NovelStage>())
                StageItems.Add(new StageItem(s, StageLabel(s), StageStatusPending));
        }
        else
        {
            for (var i = 0; i < StageItems.Count; i++)
                StageItems[i] = StageItems[i] with { Label = StageLabel(StageItems[i].Stage) };
        }
    }

    private void UpdateStatus(NovelStage stage, string status)
    {
        for (var i = 0; i < StageItems.Count; i++)
            if (StageItems[i].Stage == stage)
            {
                // 同值跳过，避免无谓的索引赋值（Replace 会触发 CollectionChanged，且枚举期间赋值会导致枚举器失效）
                if (StageItems[i].Status == status) return;
                // 运行中显示进度条（Progress 为 null 时不确定进度）；非运行状态隐藏进度条并清空进度
                StageItems[i] = StageItems[i] with
                {
                    Status = status,
                    IsRunning = status == StageStatusRunning,
                    Progress = null,
                    ProgressText = null,
                };
            }
        OnPropertyChanged(nameof(CanRestartPipeline));
    }

    /// <summary>更新指定阶段的进度条（仅运行中阶段；done/total ≤0 时为不确定进度）。</summary>
    private void UpdateStageProgress(NovelStage stage, int done, int total, string? text)
    {
        double? progress = total > 0 ? Math.Clamp((double)done / total, 0, 1) : null;
        for (var i = 0; i < StageItems.Count; i++)
            if (StageItems[i].Stage == stage)
            {
                StageItems[i] = StageItems[i] with { Progress = progress, ProgressText = text };
                return;
            }
    }

    /// <summary>把服务层分批进度回调封送到 UI 线程：实时追加本批结果到输出区 + 更新当前阶段进度条。
    /// 通过会话代际 + 当前阶段双重校验，丢弃停止/换阶段后迟到的回调，避免污染新会话输出。</summary>
    private void DispatchBatchProgress(NovelBatchProgress p, int gen, NovelStage stage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (gen != _pipelineGen || !IsExecuting || _currentResult != null) return;
            if (_currentStageIndex >= _enabledStages.Count || _enabledStages[_currentStageIndex] != stage) return;
            if (!string.IsNullOrEmpty(p.Text)) EditableText += p.Text;
            UpdateStageProgress(stage, p.Done, p.Total, p.ProgressText);
        });
    }

    private string Get(string key, string def = "") => _settings?.Get(key) ?? def;

    private async Task LoadAsync()
    {
        try
        {
            await _settings!.LoadAsync();

            var text = _settings.Get(KeyNovelText);
            if (!string.IsNullOrEmpty(text)) NovelText = text;
            EnableConcept = Get(KeyEnableConcept, "1") == "1";
            EnableCharacters = Get(KeyEnableCharacters, "1") == "1";
            EnableWorldbuilding = Get(KeyEnableWorldbuilding, "1") == "1";
            EnableTreatment = Get(KeyEnableTreatment, "1") == "1";
            EnableAssetScan = Get(KeyEnableAssetScan, "1") == "1";
            EnableShotPlanning = Get(KeyEnableShotPlanning, "1") == "1";
            EnablePrompts = Get(KeyEnablePrompts, "1") == "1";

            if (int.TryParse(_settings.Get(KeyOutputFormat), out var fmt)) OutputFormatIndex = fmt;
            if (int.TryParse(_settings.Get(KeyGranularity), out var gr)) GranularityIndex = gr;
            if (int.TryParse(_settings.Get(KeyReviewMode), out var rm)) ReviewModeIndex = rm;
            if (int.TryParse(_settings.Get(KeyOutputLanguage), out var ol) && ol is 0 or 1) OutputLanguageIndex = ol;
            EnableNoRefT2VA = _settings.Get(KeyNoRefT2VA) == "1";
            AssetLlmClassify = _settings.Get(KeyAssetLlmClassify) == "1";
            KnowledgeBaseEnabled = _settings.Get(KeyKbEnabled) == "1";
            var kp = _settings.Get(KeyKbPath);
            if (!string.IsNullOrEmpty(kp)) KnowledgeBasePath = kp;
            var ad = _settings.Get(KeyAssetDir);
            if (!string.IsNullOrEmpty(ad)) AssetDirectory = ad;
            EnableGapPrompt = _settings.Get(KeyGapPrompt) != "0";
            if (int.TryParse(_settings.Get(KeyImageBatch), out var ib) && ib > 0) ImageBatchSize = ib;
            if (int.TryParse(_settings.Get(KeyVoiceBatch), out var vb) && vb > 0) VoiceBatchSize = vb;
            var ar = _settings.Get(KeyAspectRatio);
            if (!string.IsNullOrEmpty(ar)) AspectRatio = ar;
            if (double.TryParse(_settings.Get(KeyDefaultShotSeconds), out var dss) && dss > 0) DefaultShotSeconds = dss;

            _settingsLoaded = true;

            // 小说文本未变时恢复上次流水线进度（已确认/已跳过阶段）
            TryRestorePipelineCache();

            await ReloadProvidersAsync();
        }
        catch { }
    }

    private NovelPromptOptions BuildOptions() => new()
    {
        EnableConcept = EnableConcept,
        EnableCharacters = EnableCharacters,
        EnableWorldbuilding = EnableWorldbuilding,
        EnableTreatment = EnableTreatment,
        EnableAssetScan = EnableAssetScan,
        EnableShotPlanning = EnableShotPlanning,
        EnablePrompts = EnablePrompts,
        OutputFormat = (NovelOutputFormat)OutputFormatIndex,
        Granularity = (NovelGranularity)GranularityIndex,
        ReviewMode = (NovelReviewMode)ReviewModeIndex,
        OutputLanguage = (NovelOutputLanguage)OutputLanguageIndex,
        EnableNoRefT2VA = EnableNoRefT2VA,
        AssetLlmClassify = AssetLlmClassify,
        KnowledgeBaseEnabled = KnowledgeBaseEnabled,
        KnowledgeBasePath = KnowledgeBasePath ?? "",
        AssetDirectory = AssetDirectory ?? "",
        EnableGapPrompt = EnableGapPrompt,
        ImageBatchSize = ImageBatchSize > 0 ? ImageBatchSize : 5,
        VoiceBatchSize = VoiceBatchSize > 0 ? VoiceBatchSize : 10,
        AspectRatio = string.IsNullOrWhiteSpace(AspectRatio) ? "16:9" : AspectRatio,
        DefaultShotSeconds = DefaultShotSeconds > 0 ? DefaultShotSeconds : 6,
        ProviderId = SelectedProvider?.Id ?? "",
        ModelName = SelectedModel?.ModelName ?? "",
    };

    [RelayCommand]
    private async Task StartPipelineAsync()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(NovelText))
        {
            ShowToast(_local["NovelSourceRequired"] ?? "请先输入小说原文", "");
            return;
        }
        _enabledStages = BuildEnabledStages();
        if (_enabledStages.Count == 0)
        {
            ShowToast(_local["NovelNoStageSelected"] ?? "请至少开启一个阶段", "");
            return;
        }
        PromptCraft.Service.LogService.Instance.Info($"小说流水线开始：启用阶段 {string.Join(",", _enabledStages)}", "Novel");

        IsFinished = false;
        IsRunning = true;
        IsExecuting = false;
        EditableText = "";
        ErrorMessage = "";
        _viewingHistoryStage = null;
        _gapBatchSections.Clear();
        _gapBatchIndex = -1;
        _showingScanResult = true;
        GapOffset = 0;
        GapTotal = 0;
        RefreshGapButtons();
        RefreshHistoryViewState();
        // 重置全部状态为待执行，被跳过的标已跳过（已确认阶段保持已确认，供停止/失败后从中断处继续）
        // 注意：UpdateStatus 通过索引赋值修改 StageItems（触发 Replace），必须枚举副本，否则枚举期间修改集合会抛 InvalidOperationException
        foreach (var item in StageItems.ToList())
        {
            if (_snapshots.ContainsKey(item.Stage) && item.Status == StageStatusConfirmed)
                continue;
            UpdateStatus(item.Stage, _enabledStages.Contains(item.Stage) ? StageStatusPending : StageStatusSkipped);
        }

        _pipelineGen++;
        _cts = new CancellationTokenSource();

        // 断点续跑：存在已确认阶段（缓存恢复 或 本次停止/失败后重新开始）→ 从第一个未确认阶段继续
        var hasConfirmedStages = StageItems.Any(x => x.Status == StageStatusConfirmed);
        if (_snapshots.Count > 0 && hasConfirmedStages)
        {
            // 用当前界面选项覆盖缓存/旧选项（用户可能调整过格式/粒度/批次等）
            _ctx.Options = BuildOptions();
            _currentStageIndex = 0;
            while (_currentStageIndex < _enabledStages.Count)
            {
                var st = StageItems.FirstOrDefault(x => x.Stage == _enabledStages[_currentStageIndex])?.Status ?? "";
                if (st != StageStatusConfirmed && st != StageStatusSkipped) break;
                _currentStageIndex++;
            }
            if (_currentStageIndex >= _enabledStages.Count)
            {
                // 全部阶段已完成：直接收尾
                PromptCraft.Service.LogService.Instance.Info("断点恢复：全部阶段已完成", "Novel");
                _restoredFromCache = false;
                FinishPipeline();
                return;
            }
            // 已确认阶段的状态已在重置循环中保持，这里仅重置未开始阶段为待执行
            for (var i = _currentStageIndex; i < _enabledStages.Count; i++)
                UpdateStatus(_enabledStages[i], StageStatusPending);
            PromptCraft.Service.LogService.Instance.Info($"断点续跑：从第 {_currentStageIndex + 1} 个阶段 {_enabledStages[_currentStageIndex]} 继续", "Novel");
            _restoredFromCache = false;
            await RunCurrentStageAsync();
            return;
        }

        // 全新开始
        _ctx = new NovelPipelineContext { NovelText = NovelText ?? "", Options = BuildOptions() };
        _currentStageIndex = 0;
        _snapshots.Clear();
        await RunCurrentStageAsync();
    }

    private async Task RunCurrentStageAsync()
    {
        if (_currentStageIndex >= _enabledStages.Count)
        {
            FinishPipeline();
            return;
        }
        var stage = _enabledStages[_currentStageIndex];
        var gen = _pipelineGen; // 捕获本次执行所属的会话代际，收尾前校验，防止旧会话污染新会话
        CurrentStageLabel = StageLabel(stage);
        StatusText = string.Format(_local["NovelRunningStage"] ?? "正在执行：{0}", CurrentStageLabel);
        UpdateStatus(stage, StageStatusRunning);
        _currentResult = null;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanGoBack));
        ErrorMessage = "";
        EditableText = "";
        PromptCraft.Service.LogService.Instance.Info($"执行阶段 {stage} 开始", "Novel");
        if(_cts == null)
            _cts = new CancellationTokenSource();
        try
        {
            IsExecuting = true;
            // 执行间隙允许修改资产/知识库路径，每次执行前把最新值同步进上下文
            _ctx.Options.AssetDirectory = AssetDirectory ?? "";
            _ctx.Options.KnowledgeBaseEnabled = KnowledgeBaseEnabled;
            _ctx.Options.KnowledgeBasePath = KnowledgeBasePath ?? "";
            switch (stage)
            {
                case NovelStage.Concept:
                case NovelStage.Characters:
                case NovelStage.Worldbuilding:
                case NovelStage.Treatment:
                    var r = await _svc!.RunStageAsync(stage, _ctx, _cts!.Token);
                    _currentResult = r;
                    EditableText = r.Text;
                    break;
                case NovelStage.AssetScan:
                    var a = await _svc!.ScanAssetsAsync(_ctx, _cts!.Token);
                    _ctx.ConfirmedAssets = a.ConfirmedAssets.ToList();
                    _currentResult = new NovelStageResult { Stage = NovelStage.AssetScan, Text = a.RawText, AssetScan = a };
                    _gapBatchSections.Clear();
                    _gapBatchIndex = -1;
                    _showingScanResult = true;
                    _assetScanText = a.RawText;
                    GapOffset = 0;
                    GapTotal = a.Gaps.Count + a.VoiceGaps.Count;
                    EditableText = a.RawText;
                    RefreshGapButtons();
                    break;
                case NovelStage.ShotPlanning:
                    var p = await _svc!.PlanShotsAsync(_ctx, _cts!.Token,
                        pg => DispatchBatchProgress(pg, gen, NovelStage.ShotPlanning));
                    _currentResult = new NovelStageResult { Stage = NovelStage.ShotPlanning, Text = p.RawText, ShotPlan = p };
                    EditableText = p.RawText;
                    break;
                case NovelStage.Prompts:
                    var prs = (await _svc!.GeneratePromptsAsync(_ctx, _cts!.Token,
                        pg => DispatchBatchProgress(pg, gen, NovelStage.Prompts))).ToList();
                    _ctx.Prompts = prs;
                    var prsText = BuildPromptsText(prs);
                    _currentResult = new NovelStageResult { Stage = NovelStage.Prompts, Text = prsText };
                    EditableText = prsText;
                    break;
            }
            UpdateStatus(stage, StageStatusReview);
            PromptCraft.Service.LogService.Instance.Info($"执行阶段 {stage} 完成，输出 {EditableText.Length} 字符", "Novel");
            StatusText = string.Format(_local["NovelReviewStage"] ?? "请检查并修改【{0}】后确认", CurrentStageLabel);
        }
        catch (OperationCanceledException)
        {
            if (gen != _pipelineGen) return; // 已被新会话取代（停止后立即重启），放弃收尾，不触碰新会话状态
            PromptCraft.Service.LogService.Instance.Warn($"阶段 {stage} 被取消", "Novel");
            UpdateStatus(stage, StageStatusPending);
            // 取消即终止整个流程，允许重新开始（Stop 已先行复位，此处幂等）
            TerminatePipelineCore(_local["NovelStopped"] ?? "已停止", _local["NovelStopped"] ?? "已停止");
        }
        catch (Exception ex)
        {
            if (gen != _pipelineGen) return; // 已被新会话取代（停止后立即重启），放弃收尾
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? (_local["NovelStageFailed"] ?? "阶段执行失败") : ex.Message;
            PromptCraft.Service.LogService.Instance.Error($"阶段 {stage} 失败：{msg}", "Novel", ex);
            UpdateStatus(stage, StageStatusPending);
            // 调用失败（如 AI 地址链接中断）立即终止整个流程，避免卡死无法停止/无法重新开始
            TerminatePipelineCore(msg, _local["NovelStageFailed"] ?? "阶段执行失败");
            ShowToast(msg, "");
        }
        finally
        {
            // 已被新会话取代时，旧会话的 finally 不再复位任何共享状态
            if (gen == _pipelineGen)
            {
                IsExecuting = false;
                // 阶段执行完成/失败后自动退出历史查看，保留当前阶段输出与状态提示
                if (IsViewingHistory)
                    RestorePipelineViewCore(restoreTextAndStatus: false);
                OnPropertyChanged(nameof(CanConfirm));
                OnPropertyChanged(nameof(CanRerun));
                OnPropertyChanged(nameof(CanGoBack));
                RefreshGapButtons();
            }
        }
    }

    private string BuildPromptsText(IReadOnlyList<PromptResult> prs)
    {
        var sb = new StringBuilder();
        if (ReviewModeIndex == 0)
        {
            foreach (var p in prs)
            {
                sb.AppendLine($"## {p.ShotId}（{p.SourceScene}）· {p.Model}");
                sb.AppendLine(p.Prompt);
                sb.AppendLine();
            }
        }
        else
        {
            foreach (var g in prs.GroupBy(p => p.SourceScene))
            {
                sb.AppendLine($"# 场次 {g.Key} · {g.First().Model}");
                foreach (var p in g)
                {
                    sb.AppendLine($"## {p.ShotId}");
                    sb.AppendLine(p.Prompt);
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private void FinishPipeline()
    {
        IsFinished = true;
        IsRunning = false;
        StatusText = _local["NovelDone"] ?? "全部阶段完成，可在结果区复制或保存。";
        CurrentStageLabel = "";
        _currentResult = null;
        PromptCraft.Service.LogService.Instance.Info("小说流水线全部完成", "Novel");
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanGoBack));
    }

    [RelayCommand]
    private async Task ConfirmStageAsync()
    {
        if (IsFinished) return;
        // 历史查看中「确认并继续」= 从所查看的已确认阶段之后继续（跳过其后已确认/已跳过阶段，运行第一个待执行阶段）
        if (IsViewingHistory && _viewingHistoryStage is { } hs)
        {
            if (IsExecuting) return; // 阶段执行中禁止从历史视图推进，避免并发执行
            var idx = IndexOfStage(hs);
            if (idx < 0) return;
            _viewingHistoryStage = null;
            _currentStageIndex = idx + 1;
            while (_currentStageIndex < _enabledStages.Count)
            {
                var st = StageItems.FirstOrDefault(x => x.Stage == _enabledStages[_currentStageIndex])?.Status ?? "";
                if (st != StageStatusConfirmed && st != StageStatusSkipped) break;
                _currentStageIndex++;
            }
            RefreshHistoryViewState();
            if (_currentStageIndex >= _enabledStages.Count)
            {
                FinishPipeline();
                return;
            }
            PromptCraft.Service.LogService.Instance.Info($"从历史查看阶段 {hs} 继续流水线", "Novel");
            await RunCurrentStageAsync();
            return;
        }
        if (_currentResult == null) return;
        // 处于历史查看时先退出，恢复当前流程显示，避免把历史内容提交为当前阶段结果
        if (IsViewingHistory) RestorePipelineViewCore(restoreTextAndStatus: true);
        var stage = _enabledStages[_currentStageIndex];
        switch (stage)
        {
            case NovelStage.Concept: _ctx.Concept = EditableText; break;
            case NovelStage.Characters: _ctx.Characters = EditableText; break;
            case NovelStage.Worldbuilding: _ctx.Worldbuilding = EditableText; break;
            case NovelStage.Treatment: _ctx.Treatment = EditableText; break;
            case NovelStage.AssetScan:
                _ctx.ConfirmedAssets = _currentResult.AssetScan?.ConfirmedAssets.ToList() ?? new List<string>();
                break;
            case NovelStage.ShotPlanning:
                if (_currentResult.ShotPlan != null)
                {
                    _currentResult.ShotPlan.RawText = EditableText ?? "";
                    if (_currentResult.ShotPlan.Shots.Count == 0)
                    {
                        // 未解析出任何镜头：不推进，提示修正或重跑
                        PromptCraft.Service.LogService.Instance.Warn("镜头规划确认被拦截：解析出 0 个镜头", "Novel");
                        ShowToast(_local["NovelShotPlanEmpty"] ?? "镜头规划未解析出任何镜头，请修正镜头表或重跑本阶段后再确认", "");
                        return;
                    }
                    _ctx.ShotPlan = _currentResult.ShotPlan;
                }
                break;
            case NovelStage.Prompts: break; // 最终产物，无需回写 ctx
        }
        // 「返回上一步」后若未修改本阶段文本，下一阶段可直接回显上次结果而不重跑
        var unchangedFromSnapshot = _snapshots.TryGetValue(stage, out var oldSnap)
            && string.Equals(oldSnap.EditableText, EditableText ?? "", StringComparison.Ordinal);
        UpdateStatus(stage, StageStatusConfirmed);
        PromptCraft.Service.LogService.Instance.Info($"确认阶段 {stage}", "Novel");
        _snapshots[stage] = BuildSnapshot();
        SavePipelineCache();
        _currentResult = null;
        _currentStageIndex++;
        if (_currentStageIndex >= _enabledStages.Count)
        {
            FinishPipeline();
            return;
        }
        // 回退场景：上一阶段未修改且下一阶段已有快照 → 回显，不重跑
        var next = _enabledStages[_currentStageIndex];
        if (unchangedFromSnapshot && _snapshots.TryGetValue(next, out var nextSnap))
        {
            PromptCraft.Service.LogService.Instance.Info($"阶段 {stage} 未修改，回显 {next} 上次执行结果（不重跑）", "Novel");
            CurrentStageLabel = StageLabel(next);
            RestoreSnapshot(nextSnap);
            UpdateStatus(next, StageStatusReview);
            StatusText = string.Format(_local["NovelReviewStage"] ?? "请检查并修改【{0}】后确认", CurrentStageLabel);
            return;
        }
        await RunCurrentStageAsync();
    }

    [RelayCommand]
    private async Task RerunStageAsync()
    {
        // 历史查看中「重跑本阶段」= 重跑所查看的已确认阶段
        if (IsViewingHistory && _viewingHistoryStage is { } hs)
        {
            if (IsExecuting) return; // 阶段执行中禁止从历史视图重跑，避免并发执行
            var idx = IndexOfStage(hs);
            if (idx < 0) return;
            _viewingHistoryStage = null;
            _currentStageIndex = idx;
            RefreshHistoryViewState();
            PromptCraft.Service.LogService.Instance.Info($"重跑阶段 {hs}（从历史查看触发）", "Novel");
            ClearConfirmed(hs);
            UpdateStatus(hs, StageStatusPending);
            _currentResult = null;
            await RunCurrentStageAsync();
            return;
        }
        if (_currentResult == null) return;
        // 处于历史查看时先退出，恢复当前流程显示，再重跑当前阶段
        if (IsViewingHistory) RestorePipelineViewCore(restoreTextAndStatus: true);
        var stage = _enabledStages[_currentStageIndex];
        PromptCraft.Service.LogService.Instance.Info($"重跑阶段 {stage}", "Novel");
        ClearConfirmed(stage);
        _currentResult = null;
        await RunCurrentStageAsync();
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        // 历史查看中「返回上一步」= 回到所查看阶段的上一步（回显上次确认结果，不重跑）
        if (IsViewingHistory && _viewingHistoryStage is { } hs)
        {
            if (IsExecuting) return; // 阶段执行中禁止从历史视图回退，避免并发执行
            var idx = IndexOfStage(hs);
            if (idx <= 0) return;
            var hsPrevStage = _enabledStages[idx - 1];
            _viewingHistoryStage = null;
            _currentStageIndex = idx - 1;
            RefreshHistoryViewState();
            PromptCraft.Service.LogService.Instance.Info($"从历史查看阶段 {hs} 返回上一步：回退到 {hsPrevStage}", "Novel");
            if (_snapshots.TryGetValue(hsPrevStage, out var hsSnap))
            {
                CurrentStageLabel = StageLabel(hsPrevStage);
                RestoreSnapshot(hsSnap);
                UpdateStatus(hsPrevStage, StageStatusReview);
                StatusText = string.Format(_local["NovelReviewStage"] ?? "请检查并修改【{0}】后确认", CurrentStageLabel);
            }
            else
            {
                // 兜底：无快照则重跑
                PromptCraft.Service.LogService.Instance.Warn($"返回上一步：阶段 {hsPrevStage} 无快照，改为重跑", "Novel");
                _currentResult = null;
                await RunCurrentStageAsync();
            }
            return;
        }
        // 处于历史查看时先退出，恢复当前流程显示，再执行返回上一步
        if (IsViewingHistory) RestorePipelineViewCore(restoreTextAndStatus: true);
        if (_currentStageIndex <= 0) return;
        var curStage = _enabledStages[_currentStageIndex];
        var prevStage = _enabledStages[_currentStageIndex - 1];
        PromptCraft.Service.LogService.Instance.Info($"返回上一步：回退到 {prevStage} 并回显上次结果（不重跑）", "Novel");
        // 保存当前阶段（未确认）的执行结果，供原样返回时回显
        if (_currentResult != null)
            _snapshots[curStage] = BuildSnapshot();
        // 当前阶段回归待执行
        UpdateStatus(curStage, StageStatusPending);
        _currentStageIndex--;
        // 回显上一阶段上次确认的结果，不重跑、不清空已确认内容
        if (_snapshots.TryGetValue(prevStage, out var snap))
        {
            CurrentStageLabel = StageLabel(prevStage);
            RestoreSnapshot(snap);
            UpdateStatus(prevStage, StageStatusReview);
            StatusText = string.Format(_local["NovelReviewStage"] ?? "请检查并修改【{0}】后确认", CurrentStageLabel);
        }
        else
        {
            // 兜底：无快照则重跑
            PromptCraft.Service.LogService.Instance.Warn($"返回上一步：阶段 {prevStage} 无快照，改为重跑", "Novel");
            _currentResult = null;
            await RunCurrentStageAsync();
        }
    }

    private void ClearConfirmed(NovelStage stage)
    {
        switch (stage)
        {
            case NovelStage.Concept: _ctx.Concept = null; break;
            case NovelStage.Characters: _ctx.Characters = null; break;
            case NovelStage.Worldbuilding: _ctx.Worldbuilding = null; break;
            case NovelStage.Treatment: _ctx.Treatment = null; break;
            case NovelStage.AssetScan: _ctx.ConfirmedAssets = new List<string>(); break;
            case NovelStage.ShotPlanning: _ctx.ShotPlan = null; break;
            case NovelStage.Prompts: _ctx.Prompts = new List<PromptResult>(); break;
        }
    }

    [RelayCommand]
    private async Task CopyResult()
    {
        if (string.IsNullOrWhiteSpace(EditableText))
        {
            ShowToast(_local["PromptEmpty"] ?? "提示词为空", "");
            return;
        }
        try
        {
            _services.GetRequiredService<IBaseClipboardService>().CopyToClipboard(EditableText);
            ShowToast(_local["ExpandCopied"] ?? "已复制", "");
        }
        catch { ShowToast(_local["CopyFailed"] ?? "复制失败", ""); }
    }

    [RelayCommand]
    private async Task SaveToLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(EditableText))
        {
            ShowToast(_local["NothingToSave"] ?? "没有可保存的内容", "");
            return;
        }
        try
        {
            await PromptCraft.ViewModels.PromptLibrary.PromptEditDialogOpener.OpenForExpandAsync(_services, EditableText, NovelText ?? "");
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Error(_local["OpenSaveDialogFailedLog"] ?? "打开保存对话框失败", "Novel", ex);
        }
    }

    [RelayCommand]
    private async Task GenerateGapPromptsAsync()
    {
        var scan = _currentResult?.AssetScan;
        if (scan == null || IsExecuting || IsGeneratingGaps || !EnableGapPrompt) return;
        if (GapOffset >= GapTotal) return;
        IsGeneratingGaps = true;
        try
        {
            var prevOffset = GapOffset;
            _gapCts?.Dispose();
            _gapCts = new CancellationTokenSource();
            var batch = await _svc!.GenerateGapPromptsAsync(_ctx, scan.Gaps, scan.VoiceGaps, GapOffset,
                _gapCts.Token);
            GapOffset = batch.Processed;
            GapTotal = batch.Total;
            if (batch.Prompts.Count > 0)
            {
                // 切到批次视图前保留扫描结果文本（含用户修改）
                if (_showingScanResult) _assetScanText = EditableText ?? "";
                var section = BuildGapBatchSection(batch);
                _gapBatchSections.Add(section);
                _gapBatchIndex = _gapBatchSections.Count - 1;
                _showingScanResult = false;
                EditableText = section;
                if (!batch.HasMore)
                    ShowToast(_local["NovelGapPromptsDone"] ?? "缺口提示词已全部生成，可复制去 ComfyUI / VoiceStudio 使用", "");
            }
            else
            {
                // 本批未生成有效提示词：不推进进度（保留当前批），输出区提示失败，可点击「生成提示词」重新生成本批
                if (_showingScanResult) _assetScanText = EditableText ?? "";
                GapOffset = prevOffset;
                _showingScanResult = false;
                EditableText = string.Format(_local["NovelGapBatchFailedTip"] ?? "【缺口提示词批次·{0}】\n本批生成失败，未生成有效提示词。已保留进度，点击「生成提示词」重新生成本批。", GapProgressText);
            }
            RefreshGapButtons();
        }
        catch (OperationCanceledException)
        {
            // 用户点击「停止」：保留当前进度，不当作错误提示
            PromptCraft.Service.LogService.Instance.Info("用户停止缺口提示词生成", "Novel");
            ShowToast(_local["NovelGapStopped"] ?? "已停止生成缺口提示词，可点击「生成提示词」继续", "");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ShowToast(ex.Message, "");
        }
        finally
        {
            IsGeneratingGaps = false;
            _gapCts?.Dispose();
            _gapCts = null;
        }
    }

    /// <summary>停止当前正在生成的缺口提示词批次（仅在生成中显示/可用）。</summary>
    [RelayCommand]
    private void StopGapGeneration()
    {
        if (!IsGeneratingGaps) return;
        PromptCraft.Service.LogService.Instance.Info("用户请求停止缺口提示词生成", "Novel");
        _gapCts?.Cancel();
    }

    /// <summary>重新生成缺口提示词：清空已生成批次与进度，从头重新生成（已生成过批次时显示该按钮）。</summary>
    [RelayCommand]
    private async Task RegenerateGapPromptsAsync()
    {
        var scan = _currentResult?.AssetScan;
        if (scan == null || IsExecuting || IsGeneratingGaps || !EnableGapPrompt) return;
        if (GapTotal == 0) return;
        PromptCraft.Service.LogService.Instance.Info("用户重新生成缺口提示词：清空已生成批次后从头生成", "Novel");
        // 清空批次视图与进度（保留扫描结果文本）
        if (_showingScanResult) _assetScanText = EditableText ?? "";
        _gapBatchSections.Clear();
        _gapBatchIndex = -1;
        _showingScanResult = true;
        GapOffset = 0;
        GapTotal = 0;
        EditableText = _assetScanText;
        RefreshGapButtons();
        await GenerateGapPromptsAsync();
    }

    // ---- 资产生图命令（自动生成 → pending 验收）----
    /// <summary>
    /// 对「当前缺口批次」中的图片提示词逐条自动生成资产图（v2 #4 半自动模板制）。
    /// 每行格式「资产名：提示词」：同名/模糊匹配到资产目录已有图片时自动走图生图（保一致性），否则文生图；
    /// 提交前环境自检（ComfyUI 可达/标准节点/模型候选）不通过即 fail-fast 跳过该条并提示，不提交。
    /// </summary>
    [RelayCommand]
    private async Task GenerateAssetImagesAsync()
    {
        var scan = _currentResult?.AssetScan;
        if (scan == null || _gapBatchIndex < 0 || _gapBatchIndex >= _gapBatchSections.Count) return;
        if (IsExecuting || IsGeneratingGaps || IsGeneratingAssets) return;

        // 解析当前批次提示词行（「资产名：提示词」），剔除音色缺口行（需在 VoiceStudio 生成）
        var voiceNames = new HashSet<string>(scan.VoiceGaps.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
        var lines = _gapBatchSections[_gapBatchIndex].Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("【") && !l.StartsWith("(") && !l.StartsWith("（"))
            .Select(l => new { Name = GapLineName(l), Prompt = GapLinePrompt(l) })
            .Where(x => x.Name.Length > 0 && x.Prompt.Length > 0 && !voiceNames.Contains(x.Name))
            .ToList();
        if (lines.Count == 0)
        {
            ShowToast(_local["NovelAssetGenNoImageGaps"] ?? "当前批次没有可自动生成的图片提示词（音色缺口请到 VoiceStudio 手动生成）", "");
            return;
        }

        var assetGen = _services.GetRequiredService<AssetImageGenerationService>();
        IsGeneratingAssets = true;
        AssetGenStatus = _local["NovelAssetGenProbing"] ?? "正在探测 ComfyUI 环境…";
        try
        {
            foreach (var item in lines)
            {
                if (IsGeneratingAssets == false) break; // 用户停止（停止按钮复用 StopGapGeneration 不可行，此处仅防御）
                var reference = FindReferenceAsset(item.Name);
                AssetGenStatus = string.Format(_local["NovelAssetGenProgress"] ?? "正在生成「{0}」（{1}）…",
                    item.Name, reference == null
                        ? (_local["NovelAssetGenT2I"] ?? "文生图")
                        : (_local["NovelAssetGenI2I"] ?? "图生图"));
                var request = new AssetGenRequest
                {
                    PositivePrompt = item.Prompt,
                    AssetName = item.Name,
                    AspectRatio = string.IsNullOrWhiteSpace(AspectRatio) ? "16:9" : AspectRatio,
                    ReferenceImagePath = reference,
                };
                var plan = await assetGen.PreparePlanAsync(request);
                if (!plan.Ready)
                {
                    var err = string.Join(" ", plan.Errors);
                    AssetGenStatus = string.Format(_local["NovelAssetGenSkipped"] ?? "「{0}」已跳过：{1}", item.Name, err);
                    ShowToast(AssetGenStatus, "");
                    continue; // fail-fast：不提交
                }
                var result = await assetGen.GenerateAsync(plan, s => AssetGenStatus = s);
                if (!result.Success)
                {
                    AssetGenStatus = string.Format(_local["NovelAssetGenFailed"] ?? "「{0}」生成失败：{1}", item.Name, result.Error);
                    ShowToast(result.Error ?? AssetGenStatus, "");
                    continue;
                }
                foreach (var a in result.Assets)
                {
                    var pi = new PendingAssetItem(a.AssetName, a.PendingPath);
                    pi.LoadPreview();
                    PendingAssets.Add(pi);
                }
                RefreshAssetGenUi();
            }
            AssetGenStatus = PendingAssets.Count > 0
                ? string.Format(_local["NovelAssetGenAwaitAccept"] ?? "已生成 {0} 张，请在下方验收（确认 → 移入资产目录 / 删除）。", PendingAssets.Count)
                : (_local["NovelAssetGenNoOutput"] ?? "本轮未生成任何资产图。");
        }
        catch (Exception ex)
        {
            AssetGenStatus = ex.Message;
            ShowToast(ex.Message, "");
        }
        finally
        {
            IsGeneratingAssets = false;
        }
    }

    /// <summary>验收：确认生成结果 → 移入资产目录。</summary>
    [RelayCommand]
    private async Task ConfirmAssetAsync()
    {
        var item = SelectedPendingAsset;
        if (item == null || string.IsNullOrWhiteSpace(AssetDirectory)) return;
        var assetGen = _services.GetRequiredService<AssetImageGenerationService>();
        var final = await assetGen.AcceptAssetAsync(item.PendingPath, item.AssetName, AssetDirectory);
        if (final != null)
        {
            PendingAssets.Remove(item);
            AssetGenStatus = string.Format(_local["NovelAssetGenAccepted"] ?? "已保存到资产目录：{0}", Path.GetFileName(final));
            ShowToast(AssetGenStatus, "");
            RefreshAssetGenUi();
        }
        else
        {
            AssetGenStatus = _local["NovelAssetGenNoDir"] ?? "保存失败：资产目录无效，请先选择资产目录。";
            ShowToast(AssetGenStatus, "");
        }
    }

    /// <summary>验收：拒绝生成结果 → 删除 pending 文件。</summary>
    [RelayCommand]
    private void RejectAsset()
    {
        var item = SelectedPendingAsset;
        if (item == null) return;
        _services.GetRequiredService<AssetImageGenerationService>().RejectAsset(item.PendingPath);
        PendingAssets.Remove(item);
        AssetGenStatus = _local["NovelAssetGenRejected"] ?? "已删除该生成结果。";
        RefreshAssetGenUi();
    }

    /// <summary>从「资产名：提示词」行提取资产名（兼容中文/英文冒号）。</summary>
    private static string GapLineName(string line)
    {
        var t = line.Trim();
        var idx = t.IndexOf('：');
        if (idx <= 0) idx = t.IndexOf(':');
        return idx > 0 ? t.Substring(0, idx).Trim() : t;
    }

    /// <summary>从「资产名：提示词」行提取提示词正文。</summary>
    private static string GapLinePrompt(string line)
    {
        var t = line.Trim();
        var idx = t.IndexOf('：');
        if (idx <= 0) idx = t.IndexOf(':');
        return idx > 0 && idx < t.Length - 1 ? t.Substring(idx + 1).Trim() : "";
    }

    /// <summary>资产目录中同名/包含匹配的已有图片 → 作为图生图参考图（角色后续变体保一致性）；匹配不到走文生图。</summary>
    private string? FindReferenceAsset(string assetName)
    {
        if (string.IsNullOrWhiteSpace(AssetDirectory) || !Directory.Exists(AssetDirectory) || string.IsNullOrWhiteSpace(assetName))
            return null;
        try
        {
            return Directory.EnumerateFiles(AssetDirectory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => AssetImageExts.Contains(Path.GetExtension(f)) && FileMatches(f, assetName));
        }
        catch { return null; }
    }

    private static bool FileMatches(string filePath, string assetName)
    {
        var n = Path.GetFileNameWithoutExtension(filePath);
        return n.Contains(assetName, StringComparison.OrdinalIgnoreCase)
            || assetName.Contains(n, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> AssetImageExts = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    [RelayCommand]
    private void GoPrevGapBatch()
    {
        if (_gapBatchIndex <= 0) return;
        if (_showingScanResult) _assetScanText = EditableText ?? "";
        _gapBatchIndex--;
        _showingScanResult = false;
        EditableText = _gapBatchSections[_gapBatchIndex];
        RefreshGapButtons();
    }

    [RelayCommand]
    private void GoNextGapBatch()
    {
        if (_gapBatchIndex < 0 || _gapBatchIndex >= _gapBatchSections.Count - 1) return;
        if (_showingScanResult) _assetScanText = EditableText ?? "";
        _gapBatchIndex++;
        _showingScanResult = false;
        EditableText = _gapBatchSections[_gapBatchIndex];
        RefreshGapButtons();
    }

    [RelayCommand]
    private void ShowScanResult()
    {
        var scan = _currentResult?.AssetScan;
        if (scan == null) return;
        _showingScanResult = true;
        // 优先回显保留的扫描结果文本（含用户修改），兼容旧数据回落到 RawText
        EditableText = string.IsNullOrWhiteSpace(_assetScanText) ? (scan.RawText ?? "") : _assetScanText;
        RefreshGapButtons();
    }

    private string BuildGapBatchSection(GapPromptBatch batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【缺口提示词批次 · {GapProgressText}】");
        if (batch.Prompts.Count == 0)
        {
            sb.AppendLine("(本批未生成到有效提示词，请重跑或调整批大小)");
        }
        else
        {
            foreach (var p in batch.Prompts)
                sb.AppendLine(p);
        }
        return sb.ToString();
    }

    [RelayCommand]
    private async Task PickKnowledgeBasePathAsync()
    {
        var folder = await PickFolderCoreAsync(_local["NovelPickKbFolder"] ?? "选择知识库目录");
        if (!string.IsNullOrEmpty(folder)) KnowledgeBasePath = folder;
    }

    [RelayCommand]
    private async Task PickAssetDirectoryAsync()
    {
        var folder = await PickFolderCoreAsync(_local["NovelPickAssetFolder"] ?? "选择资产目录");
        if (!string.IsNullOrEmpty(folder)) AssetDirectory = folder;
    }

    private static async Task<string?> PickFolderCoreAsync(string title)
    {
        try
        {
            var provider = StorageService.GetStorageProvider();
            if (provider == null) return null;
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Debug($"选择文件夹失败：{ex.Message}", "Novel", ex);
            return null;
        }
    }

    /// <summary>终止整个流水线：释放运行状态并复位，使「开始流程」重新可用。</summary>
    private void TerminatePipelineCore(string errorMessage, string statusText)
    {
        _cts?.Dispose();
        _cts = null;
        _currentResult = null;
        IsRunning = false;
        IsExecuting = false;
        ErrorMessage = errorMessage;
        StatusText = statusText;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanGoBack));
        RefreshGapButtons();
        RefreshHistoryViewState();
    }

    [RelayCommand]
    private void Stop()
    {
        PromptCraft.Service.LogService.Instance.Info("用户手动停止小说流水线", "Novel");
        _cts?.Cancel();
        TerminatePipelineCore(_local["NovelStopped"] ?? "已停止", _local["NovelStopped"] ?? "已停止");
    }

    /// <summary>重新开始流水线：清除全部已确认阶段与断点缓存后从头执行（需二次确认，防止误删进度）。</summary>
    [RelayCommand]
    private async Task RestartPipelineAsync()
    {
        if (IsRunning) return;
        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = _local["NovelRestartConfirmText"] ?? "将清除全部已确认阶段与断点缓存并从头开始执行，确定吗？",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["NovelRestartConfirmTitle"] ?? "重新开始流水线", MinWidth = 380 });
        if (confirm is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;
        PromptCraft.Service.LogService.Instance.Info("用户重新开始小说流水线：清除全部进度与断点缓存", "Novel");
        ResetPipelineCore();
        await StartPipelineAsync();
    }

    /// <summary>清除全部阶段进度、快照、断点缓存与展示状态（供「重新开始」使用）。</summary>
    private void ResetPipelineCore()
    {
        try { if (File.Exists(PipelineCachePath)) File.Delete(PipelineCachePath); } catch { }
        _restoredFromCache = false;
        _restoredNovelHash = "";
        _snapshots.Clear();
        _ctx = new NovelPipelineContext();
        _currentResult = null;
        _currentStageIndex = 0;
        _viewingHistoryStage = null;
        _savedEditableText = "";
        _savedStatusText = "";
        _savedStageLabel = "";
        _gapBatchSections.Clear();
        _gapBatchIndex = -1;
        _showingScanResult = true;
        _assetScanText = "";
        GapOffset = 0;
        GapTotal = 0;
        PendingAssets.Clear();
        AssetGenStatus = "";
        EditableText = "";
        ErrorMessage = "";
        StatusText = "";
        CurrentStageLabel = "";
        IsFinished = false;
        foreach (var s in Enum.GetValues<NovelStage>())
            UpdateStatus(s, StageStatusPending);
        RefreshGapButtons();
        RefreshHistoryViewState();
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanRestartPipeline));
    }

    private void ShowToast(string title, string content)
    {
        try
        {
            var toastManager = _services.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
            if (toastManager == null) return;
            var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3);
            toast.Queue();
        }
        catch { }
    }
}

/// <summary>资产生图待验收项（pending 图 + 缩略图预览）。</summary>
public sealed partial class PendingAssetItem : ObservableObject
{
    public string AssetName { get; }
    public string PendingPath { get; }
    [ObservableProperty] private Bitmap? _preview;

    public PendingAssetItem(string assetName, string pendingPath)
    {
        AssetName = assetName;
        PendingPath = pendingPath;
    }

    /// <summary>在 UI 线程解码缩略图（解码失败保持空预览）。</summary>
    public void LoadPreview()
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { Preview = new Bitmap(PendingPath); } catch { }
            });
        }
        catch { }
    }
}
