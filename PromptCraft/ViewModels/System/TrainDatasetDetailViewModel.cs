using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using PromptCraft.Service;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>数据集内素材行（列表页 + 详情页共用）。</summary>
public partial class TrainMediaItem : ObservableObject
{
    public string Base { get; set; } = "";
    public string File { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string MediaKind { get; set; } = "image";
    public bool IsImage => MediaKind == "image";
    public bool IsVideo => MediaKind == "video";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _textContent = "";

    /// <summary>图片缩略图（异步加载；视频为空）。</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _thumbnail;

    public string CaptionPreview => (TextContent ?? "").Trim().Length == 0
        ? "—" : (TextContent.Trim().Length > 200 ? TextContent.Trim().Substring(0, 200) + "…" : TextContent.Trim());

    /// <summary>打标状态标签（对齐 PromptMaster：待打标/已打标/处理中/失败；文本由 VM 注入本地化）。</summary>
    public string CaptionStateDoneLabel { get; set; } = "已打标";
    public string CaptionStateTodoLabel { get; set; } = "待打标";
    public string CaptionStateRunningLabel { get; set; } = "处理中";
    public string CaptionStateErrorLabel { get; set; } = "失败";

    /// <summary>打标状态：空=待打标 / running=处理中 / done=已打标 / error=失败。</summary>
    [ObservableProperty]
    private string _captionStatus = "";

    /// <summary>素材级打标进度（0~100，模拟递增，对齐 PromptMaster media-item-progress）。</summary>
    [ObservableProperty]
    private double _captionProgress;

    public bool IsRunning => CaptionStatus == "running";

    public bool ShowDoneTag => !IsRunning && HasCaption;
    public bool ShowTodoTag => !IsRunning && !HasCaption;

    partial void OnCaptionStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ShowDoneTag));
        OnPropertyChanged(nameof(ShowTodoTag));
    }

    public string CaptionStateLabel => CaptionStatus switch
    {
        "running" => CaptionStateRunningLabel,
        "error" => CaptionStateErrorLabel,
        "done" => CaptionStateDoneLabel,
        _ => HasCaption ? CaptionStateDoneLabel : CaptionStateTodoLabel,
    };

    public bool HasCaption => !string.IsNullOrWhiteSpace(TextContent);

    partial void OnTextContentChanged(string value)
    {
        OnPropertyChanged(nameof(CaptionStateLabel));
        OnPropertyChanged(nameof(HasCaption));
        OnPropertyChanged(nameof(ShowDoneTag));
        OnPropertyChanged(nameof(ShowTodoTag));
    }
}

/// <summary>提示词长度档位（对齐反推页 LengthOption 简化版）。</summary>
public sealed record TrainLengthOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>更多设置-附加要求勾选规则（对齐 PromptMaster joy_extra_options：勾选后拼入打标提示词）。</summary>
public sealed partial class JoyExtraOption : ObservableObject
{
    public JoyExtraOption(string id, string label, string text)
    {
        Id = id;
        Label = label;
        Text = text;
    }

    public string Id { get; }
    public string Label { get; }
    public string Text { get; }

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>是否显示角色名输入（仅"人物/角色统一称呼"勾选时显示，紧贴该选项下方）。</summary>
    public bool ShowNameInput => Id == "character_name" && IsChecked;

    partial void OnIsCheckedChanged(bool value) => OnPropertyChanged(nameof(ShowNameInput));
}

/// <summary>单选下拉项（Key 写回选中值 / Label 展示）。</summary>
public sealed record TrainChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>素材网格末尾的"添加素材"卡片占位（配合隐式 DataTemplate 渲染添加按钮）。</summary>
public sealed class AddMediaCard
{
}

/// <summary>
/// 模型训练打标 - 数据集详情（1:1 对齐 PromptMaster TrainDatasetDetail）：
/// 素材库/目录扫描两种来源、批量反推打标（复用反推服务 + train 工程）、统一打标、素材增删改、运行日志。
/// </summary>
public partial class TrainDatasetDetailViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly DatasetService _ds;
    private readonly PmPromptEngineeringService _peService;
    private readonly IWorkspaceService _workspace;
    private readonly DispatcherTimer _progressTimer;

    public event Action? BackRequested;

    public TrainDatasetDetailViewModel(
        ILocalizer localizer,
        IBaseNotice baseNotice,
        IServiceProvider services,
        string datasetName)
        : base(localizer, baseNotice)
    {
        _services = services;
        _workspace = services.GetRequiredService<IWorkspaceService>();
        _ds = services.GetRequiredService<DatasetService>();
        _peService = new PmPromptEngineeringService(
            workspaceRoot: string.IsNullOrEmpty(_workspace.Root) ? null : _workspace.Root,
            dbFactory: services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PromptCraft.Data.ComfyDbContext>>());
        DatasetName = datasetName;

        _captionLang = "zh";
        TemplateItems = new List<TrainChoice>
        {
            new("train_character", _local["TrainTemplateCharacter"] ?? "训练人物/角色"),
            new("train_style", _local["TrainTemplateStyle"] ?? "训练风格"),
        };
        BuildJoyExtraOptions();
        LengthItems = new List<TrainLengthOption>
        {
            new("very_short", _local["LengthLabel_very_short"] ?? "very_short"),
            new("short", _local["LengthLabel_short"] ?? "short"),
            new("medium", _local["LengthLabel_medium"] ?? "medium"),
            new("long", _local["LengthLabel_long"] ?? "long"),
            new("very_long", _local["LengthLabel_very_long"] ?? "very_long"),
            new("custom", _local["LengthLabel_custom"] ?? "custom"),
        };
        _selectedLength = LengthItems.FirstOrDefault(x => x.Key == "medium");

        // 素材级模拟进度（对齐 PromptMaster：每 380ms 随机递增 0.8~3.5，上限 92）
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _progressTimer.Tick += (_, _) =>
        {
            foreach (var m in MediaItems.Where(x => x.IsRunning && x.CaptionProgress < 92))
                m.CaptionProgress = Math.Min(92, m.CaptionProgress + 0.8 + 3.5 * Random.Shared.NextDouble());
        };

        _ = LoadInitialAsync();
    }

    public string DatasetName { get; }
    public string Title => _local["TrainDatasetTitle"] ?? DatasetName;

    // ==================== 数据集信息 ====================

    public DatasetInfo Info { get; private set; } = new();

    [ObservableProperty]
    private bool _isScanPath;

    partial void OnIsScanPathChanged(bool value)
    {
        OnPropertyChanged(nameof(IsManagedMode));
        OnPropertyChanged(nameof(VisibleMediaPlusAdd));
    }

    [ObservableProperty]
    private string _scanPathRoot = "";

    [ObservableProperty]
    private bool _scanRecursive = true;

    partial void OnScanRecursiveChanged(bool value) { UpdateScanSummary(); _ = LoadImagesAsync(); }

    [ObservableProperty]
    private string _scanSummary = "";

    [ObservableProperty]
    private string _statusMessage = "";

    public ObservableCollection<TrainMediaItem> MediaItems { get; } = new();

    /// <summary>素材区头部：素材总数（对齐 PromptMaster "素材 (N)"）。</summary>
    public string MaterialCountText => string.Format(_local["TrainMaterialCountFormat"] ?? "素材 ({0})", MediaItems.Count);

    /// <summary>素材区头部：已选提示（对齐 PromptMaster meta 文案）。</summary>
    public string SelectionMetaText => HasSelection
        ? string.Format(_local["TrainSelectionMetaFormat"] ?? "已选 {0} 个（不选则打标全部）", SelectedCount)
        : (_local["TrainSelectionMetaEmpty"] ?? "点击卡片勾选素材；未勾选时打标全部");

    /// <summary>全选（对齐 PromptMaster 全选按钮）。</summary>
    public IRelayCommand SelectAllCommand => new RelayCommand(() =>
    {
        foreach (var m in MediaItems) m.IsSelected = true;
        RefreshSelectionState();
    });

    /// <summary>清空选择（对齐 PromptMaster 清空选择按钮）。</summary>
    public IRelayCommand ClearSelectionCommand => new RelayCommand(() =>
    {
        foreach (var m in MediaItems) m.IsSelected = false;
        RefreshSelectionState();
    });

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(MaterialCountText));
        OnPropertyChanged(nameof(SelectionMetaText));
    }

    public int SelectedCount => MediaItems.Count(x => x.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    /// <summary>素材区是否有数据（控制统一打标等按钮可用性）。</summary>
    public bool HasItems => MediaItems.Count > 0;

    /// <summary>素材库模式（目录扫描模式下隐藏添加/删除等素材管理入口）。</summary>
    public bool IsManagedMode => !IsScanPath;

    [ObservableProperty]
    private string _searchText = "";

    public IEnumerable<TrainMediaItem> VisibleMedia =>
        string.IsNullOrWhiteSpace(SearchText)
            ? MediaItems
            : MediaItems.Where(m => m.File.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>素材网格数据源：素材卡 + 末尾"添加素材"卡片（目录扫描模式只显示扫描结果，无添加入口）。</summary>
    public IEnumerable<object> VisibleMediaPlusAdd =>
        IsScanPath ? VisibleMedia.Cast<object>() : VisibleMedia.Cast<object>().Append(new AddMediaCard());

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleMedia));
        OnPropertyChanged(nameof(VisibleMediaPlusAdd));
    }

    private async Task LoadInitialAsync()
    {
        Info = _ds.GetInfo(DatasetName);
        IsScanPath = Info.MaterialMode == "scan_path";
        ScanPathRoot = Info.ScanPathRoot;
        ScanRecursive = Info.ScanRecursive;
        CaptionFileFormat = Info.CaptionFileFormat;
        OverwriteSidecar = Info.OverwriteSidecar;
        await LoadPeProfilesAsync();
        await LoadProvidersAsync();
        await LoadImagesAsync();
        UpdateScanSummary();
    }

    private void UpdateScanSummary()
    {
        if (!IsScanPath || string.IsNullOrEmpty(ScanPathRoot))
        {
            ScanSummary = "";
            return;
        }
        var (ok, _, matched, target, skipped, _) = _ds.ScanCaptionTargets(DatasetName, MediaTarget, ScanPathRoot, ScanRecursive, OverwriteSidecar, CaptionFileFormat);
        ScanSummary = ok
            ? string.Format(_local["TrainScanSummaryFormat"] ?? "扫描到 {0} 个素材，待打标 {1}，跳过 {2}", matched, target, skipped)
            : ScanSummary;
    }

    public IRelayCommand BackCommand => new RelayCommand(() => BackRequested?.Invoke());
    public IRelayCommand OpenFolderCommand => new RelayCommand(OpenFolder);
    public IRelayCommand RefreshCommand => new AsyncRelayCommand(async () => { await LoadImagesAsync(); UpdateScanSummary(); });

    // ==================== 打标参数（对齐反推页） ====================

    /// <summary>输出语言（显示 中文/English，对齐 PromptMaster）。</summary>
    public IReadOnlyList<TrainChoice> LangItems { get; } = new List<TrainChoice>
    {
        new("zh", "中文"),
        new("en", "English"),
    };

    [ObservableProperty]
    private string _captionLang = "zh";

    public TrainChoice? SelectedLang
    {
        get => LangItems.FirstOrDefault(x => x.Key == CaptionLang);
        set { if (value != null && value.Key != CaptionLang) CaptionLang = value.Key; }
    }

    partial void OnCaptionLangChanged(string value) => OnPropertyChanged(nameof(SelectedLang));

    public IReadOnlyList<TrainLengthOption> LengthItems { get; }

    [ObservableProperty]
    private TrainLengthOption? _selectedLength;

    [ObservableProperty]
    private decimal _customLenChars = 300;

    public bool IsCustomLen => SelectedLength?.Key == "custom";

    partial void OnSelectedLengthChanged(TrainLengthOption? value) => OnPropertyChanged(nameof(IsCustomLen));

    [ObservableProperty]
    private string _extraPrompt = "";

    [ObservableProperty]
    private string _captionFileFormat = "txt";

    partial void OnCaptionFileFormatChanged(string value) => _ = LoadImagesAsync();

    [ObservableProperty]
    private bool _overwriteSidecar;

    partial void OnOverwriteSidecarChanged(bool value) { UpdateScanSummary(); _ = LoadImagesAsync(); }

    [ObservableProperty]
    private string _mediaTarget = "image";

    partial void OnMediaTargetChanged(string value) => _ = LoadImagesAsync();

    partial void OnScanPathRootChanged(string value) => UpdateScanSummary();

    public IReadOnlyList<TrainChoice> MediaTargetItems { get; } = new List<TrainChoice>
    {
        new("image", "图片"),
        new("video", "视频"),
    };

    public IReadOnlyList<TrainChoice> CaptionFileFormatItems { get; } = new List<TrainChoice>
    {
        new("txt", "txt"),
        new("json", "json"),
    };

    public IReadOnlyList<TrainChoice> ModeItems { get; } = new List<TrainChoice>
    {
        new("managed", "素材库"),
        new("scan_path", "目录扫描"),
    };

    public string Mode
    {
        get => IsScanPath ? "scan_path" : "managed";
        set
        {
            var v = value == "scan_path";
            if (IsScanPath != v)
            {
                IsScanPath = v;
                SaveInfo();
                UpdateScanSummary();
                _ = LoadImagesAsync();
            }
        }
    }

    public string ModeLabel => IsScanPath
        ? (_local["TrainModeScan"] ?? "目录扫描")
        : (_local["TrainModeManaged"] ?? "素材库");

    // ---- ComboBox 选中项映射（Avalonia 无 SelectedValuePath，用 SelectedItem 双向映射到字符串键） ----
    public TrainChoice? SelectedMediaTarget
    {
        get => MediaTargetItems.FirstOrDefault(x => x.Key == MediaTarget);
        set { if (value != null && value.Key != MediaTarget) MediaTarget = value.Key; }
    }

    public TrainChoice? SelectedCaptionFileFormat
    {
        get => CaptionFileFormatItems.FirstOrDefault(x => x.Key == CaptionFileFormat);
        set { if (value != null) CaptionFileFormat = value.Key; }
    }

    public TrainChoice? SelectedMode
    {
        get => ModeItems.FirstOrDefault(x => x.Key == Mode);
        set { if (value != null) Mode = value.Key; }
    }

    public KeyValuePair<string, string> SelectedUniformOp
    {
        get => UniformOps.FirstOrDefault(x => x.Key == UniformOp);
        set { UniformOp = value.Key; }
    }

    // ---- 提示词工程（train 分类，仅启用项） ----
    public ObservableCollection<PromptEngineeringProfile> PeProfiles { get; } = new();

    [ObservableProperty]
    private PromptEngineeringProfile? _selectedPe;

    partial void OnSelectedPeChanged(PromptEngineeringProfile? value)
    {
        if (value != null) OnPropertyChanged(nameof(OutputFormatLabel));
    }

    public string OutputFormatLabel => SelectedPe?.OutputFormat switch
    {
        "prose" => _local["TrainOutputProse"] ?? "prose",
        "sd_tags" => _local["TrainOutputSdTags"] ?? "sd_tags",
        "danbooru_tags" => _local["TrainOutputDanbooruTags"] ?? "danbooru_tags",
        _ => SelectedPe?.OutputFormat ?? "",
    };

    private async Task LoadPeProfilesAsync()
    {
        PeProfiles.Clear();
        try
        {
            var list = await Task.Run(() => _peService.ListProfiles(kind: "train", enabledOnly: true));
            foreach (var p in list) PeProfiles.Add(p);
            SelectedPe = PeProfiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"加载训练提示词工程失败: {ex.Message}", "TrainDataset", ex);
        }
    }

    // ---- 打标模型（服务商两级，对齐 PromptMaster：打标模型=服务商 → 服务商模型=具体模型） ----
    public AvaloniaList<ProviderConfig> Providers { get; } = new();

    [ObservableProperty]
    private ProviderConfig? _selectedProvider;

    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        SelectedModel = value?.Models.FirstOrDefault(m => m.IsDefaultReverse)
                        ?? value?.Models.FirstOrDefault(m => m.UseForReverse)
                        ?? value?.Models.FirstOrDefault();
        OnPropertyChanged(nameof(HasProviderModels));
        OnPropertyChanged(nameof(ProviderModelPlaceholder));
        OnPropertyChanged(nameof(ProviderModelEmptyText));
    }

    [ObservableProperty]
    private ProviderModel? _selectedModel;

    /// <summary>当前服务商是否有可选的打标模型（无模型时展示"去配置"提示）。</summary>
    public bool HasProviderModels => SelectedProvider?.Models.Count > 0;

    /// <summary>服务商模型下拉占位文案（对齐 PromptMaster：有模型/无模型两种）。</summary>
    public string ProviderModelPlaceholder => HasProviderModels
        ? (_local["TrainProviderModelPlaceholder"] ?? "选择该服务下的模型")
        : (_local["TrainProviderModelPlaceholderEmpty"] ?? "请先到设置配置并勾选打标模型");

    /// <summary>服务商模型空态提示（无模型时显示在第二个下拉下方）。</summary>
    public string ProviderModelEmptyText => string.Format(
        _local["TrainProviderModelEmptyFormat"] ?? "{0} 未配置打标模型，请到设置页勾选后使用",
        SelectedProvider?.Name ?? _local["TrainProviderModelUnknown"] ?? "当前服务商");

    /// <summary>"去配置"跳转设置页（对齐 PromptMaster 去配置按钮）。</summary>
    public IRelayCommand GoSettingsCommand => new RelayCommand(() =>
    {
        LogService.Instance.Info("训练打标：跳转设置页配置打标模型", "TrainDataset");
        _noticeService.Publish(PromptCraft.Consts.Event.EventNameConst.SystemNavigatePageEvent, typeof(SettingModel));
    });

    private async Task LoadProvidersAsync()
    {
        Providers.Clear();
        try
        {
            var providerSvc = _services.GetRequiredService<IProviderService>();
            var list = await providerSvc.GetAllAsync(CancellationToken.None);
            foreach (var p in list) Providers.Add(p);
            SelectedProvider = Providers.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"加载打标服务商失败: {ex.Message}", "TrainDataset", ex);
        }
    }

    // ==================== 更多设置（对齐 PromptMaster：附加要求规则 / 自定义提示词模板 / Temperature / Top P） ====================

    [ObservableProperty]
    private double _temperature = 0.7;

    [ObservableProperty]
    private double _topP = 0.9;

    /// <summary>Temperature 数值文本（三位有效数字，可输入调整，对齐 PromptMaster el-slider show-input）。</summary>
    public string TemperatureText
    {
        get => Temperature.ToString("F3");
        set { if (double.TryParse(value, out var v)) Temperature = Math.Clamp(v, 0, 2); }
    }

    /// <summary>Top P 数值文本（三位有效数字，可输入调整）。</summary>
    public string TopPText
    {
        get => TopP.ToString("F3");
        set { if (double.TryParse(value, out var v)) TopP = Math.Clamp(v, 0, 1); }
    }

    partial void OnTemperatureChanged(double value) => OnPropertyChanged(nameof(TemperatureText));

    partial void OnTopPChanged(double value) => OnPropertyChanged(nameof(TopPText));

    /// <summary>自定义提示词模板（对齐 PromptMaster be：训练人物/角色、训练风格）。</summary>
    public IReadOnlyList<TrainChoice> TemplateItems { get; }

    /// <summary>当前选中的模板；切换后自动填充自定义提示词（对齐 PromptMaster ht()）。</summary>
    [ObservableProperty]
    private TrainChoice? _selectedTemplate;

    partial void OnSelectedTemplateChanged(TrainChoice? value)
    {
        if (value == null) return;
        ExtraPrompt = value.Key switch
        {
            "train_character" => "Describe only the scene, environment, background, pose, facial expression, camera angle, composition, lighting, and action. Do NOT describe any character appearance traits or clothing. Do not write any visible physical appearance features—such as face, facial features, body, skin, hair, body shape, or similar traits—or any garments, outfits, or clothing items.",
            "train_style" => "Describe only the subject, objects, environment, composition, pose, expression, actions, camera angle, and scene details. Remove all artistic style descriptions, rendering styles, medium descriptions, quality tags, aesthetic terms, and visual style labels. Do not mention anime, cartoon, realistic, painting, illustration, 3D render, cinematic, digital art, concept art, or similar style-related terms.",
            _ => ExtraPrompt,
        };
    }

    /// <summary>附加要求勾选规则全集（对齐 PromptMaster _e 规则表，label 中文/text 英文）。</summary>
    public ObservableCollection<JoyExtraOption> JoyExtraOptions { get; } = new();

    /// <summary>"人物/角色统一称呼"输入的名称（勾选 character_name 时显示输入框）。</summary>
    [ObservableProperty]
    private string _characterName = "";

    /// <summary>是否勾选了"人物/角色统一称呼"（控制角色名输入框显隐）。</summary>
    public bool IsCharacterNameVisible => JoyExtraOptions.Any(x => x.Id == "character_name" && x.IsChecked);

    private void BuildJoyExtraOptions()
    {
        void Add(string id, string label, string text) => JoyExtraOptions.Add(new JoyExtraOption(id, label, text));
        Add("character_name", "人物/角色统一称呼（需填写名称）", "If there is a person/character in the image you must refer to them as {name}.");
        Add("scene_only_no_character_appearance", "不写人物外貌与衣着", "Do NOT describe any character appearance traits or clothing. Do not write any visible physical appearance features—such as face, facial features, body, skin, hair, body shape, or similar traits—or any garments, outfits, or clothing items.");
        Add("no_glasses_headwear", "不描述人物的眼镜与头饰", "Do NOT describe any glasses, goggles, eyewear, sunglasses, or headwear on the person/character (including hats, helmets, headbands, crowns, and hair accessories worn on the head).");
        Add("no_unchangeable_traits", "不写不可变人物属性（种族、性别等）", "Do NOT include information about people/characters that cannot be changed (like ethnicity, gender, etc), but do still include changeable attributes (like hair style).");
        Add("no_artistic_style", "不要描述风格", "Do NOT describe artistic style, rendering style, image medium, quality tags, aesthetic terms, or visual style labels. Do not mention anime, cartoon, realistic, painting, illustration, 3D render, cinematic, digital art, concept art, or similar style-related terms.");
        Add("lighting", "包含光照信息", "Include information about lighting.");
        Add("camera_angle", "包含相机角度", "Include information about camera angle.");
        Add("watermark", "说明是否有水印", "Include information about whether there is a watermark or not.");
        Add("jpeg_artifacts", "说明是否有 JPEG 压缩痕迹", "Include information about whether there are JPEG artifacts or not.");
        Add("camera_exif", "照片需推测相机型号及光圈/快门/ISO 等", "If it is a photo you MUST include information about what camera was likely used and details such as aperture, shutter speed, ISO, etc.");
        Add("pg_only", "不含色情内容", "Do NOT include anything sexual; keep it PG.");
        Add("no_resolution", "不提及图像分辨率", "Do NOT mention the image's resolution.");
        Add("aesthetic_quality", "包含主观美学质量（低～极高）", "You MUST include information about the subjective aesthetic quality of the image from low to very high.");
        Add("composition_style", "包含构图方式（引导线、三分法、对称等）", "Include information on the image's composition style, such as leading lines, rule of thirds, or symmetry.");
        Add("no_in_image_text", "不描述图中文字", "Do NOT mention any text that is in the image.");
        Add("depth_of_field", "说明景深与背景虚实", "Specify the depth of field and whether the background is in focus or blurred.");
        Add("lighting_sources", "说明自然光或人造光源", "If applicable, mention the likely use of artificial or natural lighting sources.");
        Add("no_ambiguous", "不使用模糊用语", "Do NOT use any ambiguous language.");
        Add("sfw_rating", "标注 SFW / suggestive / NSFW", "Include whether the image is sfw, suggestive, or nsfw.");
        Add("important_only", "只描述最重要元素", "ONLY describe the most important elements of the image.");
        Add("no_artist_title", "艺术品不写作者名与作品名", "If it is a work of art, do not include the artist's name or the title of the work.");
        Add("orientation", "说明竖版/横版/方图及宽高比", "Identify the image orientation (portrait, landscape, or square) and aspect ratio if obvious.");
        Add("vulgar_slang", "使用粗俗俚语（含露骨用词）", "Use vulgar slang and profanity, such as (but not limited to) \"fucking,\" \"slut,\" \"cock,\" etc.");
        Add("blunt_phrasing", "不用委婉语，直白口语化", "Do NOT use polite euphemisms—lean into blunt, casual phrasing.");
        Add("character_ages", "提及人物/角色年龄（如适用）", "Include information about the ages of any people/characters when applicable.");
        Add("shot_type", "说明景别（特写/中景/全景等）", "Mention whether the image depicts an extreme close-up, close-up, medium close-up, medium shot, cowboy shot, medium wide shot, wide shot, or extreme wide shot.");
        Add("no_mood", "不描述情绪/氛围", "Do not mention the mood/feeling/etc of the image.");
        Add("vantage_height", "明确机位高度（平视/仰视/俯视/航拍等）", "Explicitly specify the vantage height (eye-level, low-angle worm's-eye, bird's-eye, drone, rooftop, etc.).");
        Add("watermark_must", "有水印时必须提及", "If there is a watermark, you must mention it.");
        Add("no_meta_phrases", "避免 “This image shows…” 等开场白话术", "Your response will be used by a text-to-image model, so avoid useless meta phrases like \"This image shows…\", \"You are looking at...\", etc.");
        foreach (var o in JoyExtraOptions)
            o.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsCharacterNameVisible));
    }

    private ReverseCaptionRequest BuildCaptionRequest()
    {
        // 自定义提示词 + 勾选规则（对齐 PromptMaster：extra_prompt 与 joy_extra_options 合并进提示词）
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ExtraPrompt)) parts.Add(ExtraPrompt.Trim());
        foreach (var o in JoyExtraOptions.Where(x => x.IsChecked))
        {
            parts.Add(o.Id == "character_name"
                ? o.Text.Replace("{name}", string.IsNullOrWhiteSpace(CharacterName) ? "{NAME}" : CharacterName.Trim())
                : o.Text);
        }
        return new ReverseCaptionRequest
        {
            PeId = SelectedPe?.Id,
            Type = SelectedPe?.CaptionType ?? "Descriptive",
            CaptionLang = CaptionLang,
            Len = SelectedLength?.Key ?? "medium",
            CaptionLenChars = IsCustomLen ? (int)CustomLenChars : null,
            ExtraPrompt = parts.Count > 0 ? string.Join(" ", parts) : null,
            CaptionModel = SelectedModel?.ModelName,
            MediaTarget = MediaTarget,
            CaptionFileFormat = CaptionFileFormat,
            Temperature = Temperature,
            TopP = TopP,
            WriteCaptionSidecar = true,
        };
    }

    // ==================== 素材加载 ====================

    public async Task LoadImagesAsync()
    {
        // 目录扫描模式：展示扫描出的媒体（含缩略图与 sidecar 打标文本回显）；素材库模式：读数据集目录
        var items = await Task.Run(() => IsScanPath
            ? _ds.GetScanMedia(DatasetName, MediaTarget, ScanPathRoot, ScanRecursive, CaptionFileFormat)
            : _ds.GetImages(DatasetName, CaptionFileFormat));
        var selected = new HashSet<string>(MediaItems.Where(x => x.IsSelected).Select(x => x.FilePath));
        foreach (var old in MediaItems) old.PropertyChanged -= MediaItem_PropertyChanged;
        MediaItems.Clear();
        foreach (var it in items)
        {
            var item = new TrainMediaItem
            {
                Base = it.Base,
                File = it.File,
                FilePath = it.FilePath,
                MediaKind = it.MediaKind,
                TextContent = it.TextContent,
                IsSelected = selected.Contains(it.FilePath),
                CaptionStateDoneLabel = _local["TrainCaptionStateDone"] ?? "已打标",
                CaptionStateTodoLabel = _local["TrainCaptionStateTodo"] ?? "待打标",
                CaptionStateRunningLabel = _local["TrainCaptionStateRunning"] ?? "处理中",
                CaptionStateErrorLabel = _local["TrainCaptionStateError"] ?? "失败",
            };
            item.PropertyChanged += MediaItem_PropertyChanged;
            MediaItems.Add(item);
        }
        OnPropertyChanged(nameof(VisibleMedia));
        OnPropertyChanged(nameof(VisibleMediaPlusAdd));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(MaterialCountText));
        OnPropertyChanged(nameof(SelectionMetaText));
        _ = LoadThumbnailsAsync();
    }

    /// <summary>异步加载图片缩略图（DecodeToWidth 320 省内存；失败静默，视频无缩略图）。</summary>
    private async Task LoadThumbnailsAsync()
    {
        var items = MediaItems.Where(x => x.IsImage && x.Thumbnail == null).ToList();
        foreach (var item in items)
        {
            try
            {
                var bmp = await Task.Run(() =>
                {
                    using var fs = File.OpenRead(item.FilePath);
                    return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 320);
                });
                item.Thumbnail = bmp;
            }
            catch
            {
                // 单张失败不影响整体（文件可能被外部移动/删除）
            }
        }
    }

    // ==================== 批量打标 ====================

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private int _doneCount;

    [ObservableProperty]
    private int _totalCount;

    /// <summary>开始打标按钮文案：有选中时自动变为"打标选中 (N)"（对齐 PromptMaster，无单独开关）。</summary>
    public string StartButtonText => HasSelection
        ? string.Format(_local["TrainStartSelectedFormat"] ?? "打标选中 ({0})", SelectedCount)
        : (_local["TrainStartLabel"] ?? "开始打标");

    public ObservableCollection<string> RunLogs { get; } = new();

    /// <summary>素材勾选变化后刷新计数/按钮文案/头部统计。</summary>
    private void MediaItem_PropertyChanged(object? sender, global::System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrainMediaItem.IsSelected))
        {
            RefreshSelectionState();
        }
    }

    [RelayCommand]
    private async Task StartCaptionAsync()
    {
        if (IsRunning) return;
        // 未配置打标模型：不触发打包流程，提示去配置（对齐 PromptMaster an()）
        if (SelectedProvider == null || SelectedModel == null)
        {
            var providerName = SelectedProvider?.Name ?? _local["TrainProviderModelUnknown"] ?? "当前服务商";
            var message = string.Format(
                _local["TrainModelRequiredFormat"] ?? "{0} 未配置打标模型，请到设置页配置 AI 供应商后重试",
                providerName);
            var go = await SukiMessageBox.ShowDialog(
                new SukiMessageBoxHost
                {
                    Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(4) },
                    ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
                },
                new SukiMessageBoxOptions
                {
                    Title = _local["TrainModelRequiredTitle"] ?? "未配置打标模型",
                    MinWidth = 360,
                });
            if (go is SukiMessageBoxResult r && r.Equals(SukiMessageBoxResult.OK))
            {
                LogService.Instance.Info("训练打标：未配置模型，跳转设置页", "TrainDataset");
                _noticeService.Publish(PromptCraft.Consts.Event.EventNameConst.SystemNavigatePageEvent, typeof(SettingModel));
            }
            else
            {
                StatusMessage = message;
            }
            return;
        }
        var info = Info;
        var (ok, msg, paths) = _ds.ResolveCaptionPaths(
            DatasetName, MediaTarget, HasSelection, MediaItems.Where(x => x.IsSelected).Select(x => x.FilePath), info);
        if (!ok)
        {
            StatusMessage = msg;
            return;
        }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        DoneCount = 0;
        TotalCount = paths.Count;
        ProgressText = $"0/{TotalCount}";
        RunLogs.Clear();
        AddLog(string.Format(_local["TrainBatchStartFormat"] ?? "开始批量打标，共 {0} 个素材", paths.Count));
        // 打标开始：重置素材进度状态并启动模拟进度（对齐 PromptMaster captionStatus）
        var batchSet = new HashSet<string>(paths.Select(p => p.ToLowerInvariant().Replace('/', '\\')));
        foreach (var m in MediaItems)
        {
            if (batchSet.Contains(m.FilePath.ToLowerInvariant().Replace('/', '\\')))
            {
                m.CaptionStatus = "";
                m.CaptionProgress = 0;
            }
        }
        _progressTimer.Start();
        var template = BuildCaptionRequest();
        try
        {
            var progress = new Progress<ReverseProgressEvent>(e =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var item = MediaItems.FirstOrDefault(m =>
                        string.Equals(m.FilePath, e.MediaPath, StringComparison.OrdinalIgnoreCase));
                    if (e.Status == "processing")
                    {
                        if (item != null)
                        {
                            item.CaptionStatus = "running";
                            item.CaptionProgress = Math.Max(item.CaptionProgress, 3);
                        }
                    }
                    else if (e.Status == "done")
                    {
                        DoneCount++;
                        ProgressText = $"{DoneCount}/{e.Total}";
                        AddLog($"[{e.Index + 1}/{e.Total}] ✓ {Path.GetFileName(e.MediaPath)}");
                        if (item != null)
                        {
                            item.CaptionStatus = "done";
                            item.CaptionProgress = 100;
                            item.TextContent = e.Caption ?? item.TextContent;
                        }
                    }
                    else if (e.Status == "error")
                    {
                        AddLog($"[{e.Index + 1}/{e.Total}] ✗ {Path.GetFileName(e.MediaPath)}: {e.Error}");
                        if (item != null) item.CaptionStatus = "error";
                    }
                });
            });
            var results = await _ds.StartAutoCaptionAsync(
                DatasetName, paths, template, SelectedProvider, SelectedModel, _cts.Token, progress);
            var okCount = results.Count(r => r.Success);
            var failCount = results.Count - okCount;
            AddLog(string.Format(_local["TrainBatchDoneFormat"] ?? "打标完成：成功 {0} / 共 {1}", okCount, results.Count));
            StatusMessage = okCount > 0
                ? string.Format(_local["TrainBatchDoneFormat"] ?? "打标完成：成功 {0} / 共 {1}", okCount, results.Count)
                : (_local["TrainBatchAllFailed"] ?? "全部打标失败");
            if (failCount > 0)
                AddLog(string.Format(_local["TrainBatchFailedFormat"] ?? "失败 {0} 个", failCount));
            await LoadImagesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog(_local["TrainStoppedLog"] ?? "打标已停止");
            StatusMessage = _local["TrainStoppedToast"] ?? "打标已停止";
        }
        catch (Exception ex)
        {
            AddLog($"{_local["TrainBatchErrorLog"] ?? "打标出错"}: {ex.Message}");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
            _progressTimer.Stop();
            foreach (var m in MediaItems) { m.CaptionStatus = ""; m.CaptionProgress = 0; }
        }
    }

    [RelayCommand]
    private void StopCaption()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            AddLog(_local["TrainStopRequestedLog"] ?? "已发送停止请求…");
        }
    }

    [RelayCommand]
    private async Task SaveCaptionAsync(TrainMediaItem item)
    {
        if (item == null) return;
        var (ok, msg) = _ds.SaveMaterialCaption(item.FilePath, item.TextContent, CaptionFileFormat);
        StatusMessage = ok ? (_local["TrainCaptionSaved"] ?? "已保存打标") : msg;
        LogService.Instance.Info($"保存数据集素材打标: {item.File}", "TrainDataset");
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var paths = MediaItems.Where(x => x.IsSelected).Select(x => x.FilePath).ToList();
        if (paths.Count == 0)
        {
            StatusMessage = _local["TrainNoSelection"] ?? "请先选中要删除的素材";
            return;
        }
        var (ok, count, msg) = _ds.DeleteImages(paths);
        if (ok)
        {
            StatusMessage = string.Format(_local["TrainDeletedFormat"] ?? "已删除 {0} 个素材", count);
            LogService.Instance.Info($"删除数据集素材 {count} 个: {DatasetName}", "TrainDataset");
            await LoadImagesAsync();
        }
        else
        {
            StatusMessage = msg;
        }
    }

    /// <summary>删除单个素材（素材卡右上角 ×，对齐 PromptMaster media-card__remove）。</summary>
    [RelayCommand]
    private async Task DeleteOneAsync(TrainMediaItem item)
    {
        if (item == null) return;
        var (ok, count, msg) = _ds.DeleteImages(new[] { item.FilePath });
        StatusMessage = ok ? string.Format(_local["TrainDeletedFormat"] ?? "已删除 {0} 个素材", count) : msg;
        if (ok)
        {
            LogService.Instance.Info($"删除数据集素材: {item.File}", "TrainDataset");
            await LoadImagesAsync();
        }
    }

    [RelayCommand]
    private async Task AddImagesAsync()
    {
        var top = TopLevel.GetTopLevel(Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt ? lt.MainWindow : null);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _local["TrainPickMedia"] ?? "选择要添加的素材",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(_local["TrainMediaTypeLabel"] ?? "图片/视频")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif", "*.mp4", "*.mov", "*.avi", "*.mkv", "*.webm", "*.m4v" }
                },
            },
        });
        if (files.Count == 0) return;
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var (ok, copied, msg) = _ds.AddImagesFromPaths(DatasetName, paths);
        StatusMessage = msg;
        if (ok)
        {
            LogService.Instance.Info($"数据集导入素材 {copied} 个: {DatasetName}", "TrainDataset");
            await LoadImagesAsync();
            UpdateScanSummary();
        }
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var top = TopLevel.GetTopLevel(Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt ? lt.MainWindow : null);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = _local["TrainPickFolder"] ?? "选择要导入的文件夹" });
        if (folders.Count == 0) return;
        var folder = folders[0].TryGetLocalPath();
        var (ok, copied, msg) = _ds.ImportFolder(DatasetName, folder);
        StatusMessage = msg;
        if (ok)
        {
            LogService.Instance.Info($"数据集导入文件夹 {copied} 个素材: {DatasetName}", "TrainDataset");
            await LoadImagesAsync();
            UpdateScanSummary();
        }
    }

    [RelayCommand]
    private async Task PickScanRootAsync()
    {
        var top = TopLevel.GetTopLevel(Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt ? lt.MainWindow : null);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = _local["TrainPickScanRoot"] ?? "选择扫描目录" });
        if (folders.Count == 0) return;
        ScanPathRoot = folders[0].TryGetLocalPath() ?? "";
        SaveInfo();
        UpdateScanSummary();
        _ = LoadImagesAsync();
    }

    private void SaveInfo()
    {
        var info = new DatasetInfo
        {
            MaterialMode = IsScanPath ? "scan_path" : "managed",
            ScanPathRoot = ScanPathRoot,
            ScanRecursive = ScanRecursive,
            OverwriteSidecar = OverwriteSidecar,
            CaptionFileFormat = CaptionFileFormat,
        };
        _ds.SaveInfo(DatasetName, info);
        Info = info;
        LogService.Instance.Info($"保存数据集设置: {DatasetName}", "TrainDataset");
    }

    // ==================== 统一打标 ====================

    [ObservableProperty]
    private bool _isUniformOpen;

    [ObservableProperty]
    private string _uniformText = "";

    [ObservableProperty]
    private string _uniformOp = "cover";

    [ObservableProperty]
    private string _uniformTarget = "";

    public IReadOnlyList<KeyValuePair<string, string>> UniformOps { get; } = new List<KeyValuePair<string, string>>
    {
        new("cover", "覆盖"),
        new("start", "开头"),
        new("end", "结尾"),
        new("replace", "替换"),
    };

    [RelayCommand]
    private void OpenUniform() => IsUniformOpen = true;

    [RelayCommand]
    private void CloseUniform() => IsUniformOpen = false;

    [RelayCommand]
    private async Task ApplyUniformAsync()
    {
        var (ok, count, msg) = _ds.UniformCaption(DatasetName, UniformOp, UniformText, UniformTarget, CaptionFileFormat, MediaTarget);
        StatusMessage = ok ? string.Format(_local["TrainUniformDoneFormat"] ?? "已统一打标 {0} 个素材", count) : msg;
        if (ok)
        {
            LogService.Instance.Info($"统一打标 {count} 个素材: {DatasetName} op={UniformOp}", "TrainDataset");
            IsUniformOpen = false;
            await LoadImagesAsync();
        }
    }

    // ==================== 工具 ====================

    private void OpenFolder()
    {
        var (_, dir, _) = _ds.ResolveDatasetDir(DatasetName);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            StatusMessage = _local["TrainFolderNotExist"] ?? "数据集文件夹不存在";
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void AddLog(string line)
    {
        RunLogs.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        if (RunLogs.Count > 400)
            RunLogs.RemoveAt(0);
    }
}
