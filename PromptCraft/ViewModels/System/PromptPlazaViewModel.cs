using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Service;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>广场卡片条目（瀑布流展示 + 右侧详情抽屉）。</summary>
public partial class PlazaItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Models { get; set; } = "";
    public string TagsText { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Positive { get; set; } = "";
    public string Negative { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public bool IsRecommended { get; set; }
    public int ViewCount { get; set; }
    public int CopyCount { get; set; }
    public string AuthorName { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string StatsLabel => $"{ViewCount} 浏览 · {CopyCount} 使用";
    /// <summary>作者首字（头像缺失/加载失败时的圆形占位）。</summary>
    public string AuthorInitial => AuthorName.Length > 0 ? AuthorName[..1] : "匿";

    /// <summary>远程封面（异步加载失败为 null，卡片显示占位图标；图片不裁切、高度随原图比例自适应）。</summary>
    [ObservableProperty]
    private Bitmap? _cover;

    /// <summary>远程作者头像（异步加载失败为 null，显示首字占位）。</summary>
    [ObservableProperty]
    private Bitmap? _avatar;
}

/// <summary>排序选项（对齐 PromptMaster 最新/浏览/使用）。</summary>
public sealed record PlazaSortOption(string Key, string Label);

/// <summary>
/// 提示词广场页（对齐 PromptMaster PromptPlaza）：瀑布流展示、搜索/模型筛选/排序、我的投稿、
/// 点击卡片打开右侧详情抽屉（正向/反向提示词复制 + 复制上报）。
/// 数据源为云端接口（<see cref="IPromptPlazaSource"/>，https://api.comfyit.cn/），获取方法 1:1 移植自 PromptMaster。
/// </summary>
public partial class PromptPlazaViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private const int PageSize = 50;
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly IServiceProvider _services;
    private readonly IPromptPlazaSource _source;
    private readonly IBaseClipboardService _clipboard;

    public PromptPlazaViewModel(ILocalizer localizer, IBaseNotice baseNotice, IServiceProvider services)
        : base(localizer, baseNotice)
    {
        _displayName = "PROMPTPLPLAZA";
        _icon = MaterialIconKind.Storefront;
        _index = 80;
        _sideMenu = true;
        _services = services;
        _source = services.GetRequiredService<IPromptPlazaSource>();
        _clipboard = services.GetRequiredService<IBaseClipboardService>();

        SortItems = new List<PlazaSortOption>
        {
            new("latest", _local["PromptPlazaSortLatest"] ?? "最新"),
            new("view", _local["PromptPlazaSortView"] ?? "浏览"),
            new("copy", _local["PromptPlazaSortCopy"] ?? "使用"),
        };
        _selectedSort = SortItems[0];
        _allModelLabel = _local["PromptPlazaModelAll"] ?? "All";
        // 模型筛选首项为 "全部/All"（选中时请求不传 models，服务端返回全部模型）；
        // 否则用户一旦选了具体模型就回不到"查看全部"
        ModelItems = new[] { _allModelLabel }.Concat(PlazaModels.All).ToList();
        _selectedModel = _allModelLabel;
        _ = LoadAsync();
    }

    public string Title => _local["PromptPlazaTitle"] ?? "";

    // ==================== 工具栏 ====================

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value) => _ = LoadAsync();

    /// <summary>模型筛选选项（对齐 PromptMaster plazaModels 图片+视频全量；首项"全部"选中时不传 models）。</summary>
    public IReadOnlyList<string> ModelItems { get; }

    /// <summary>"全部"选项的显示文本（用于选中判断：等于它则请求不传 models）。</summary>
    private readonly string _allModelLabel;

    [ObservableProperty]
    private string? _selectedModel;

    partial void OnSelectedModelChanged(string? value) => _ = LoadAsync();

    /// <summary>排序选项（最新/浏览/使用）。</summary>
    public IReadOnlyList<PlazaSortOption> SortItems { get; }

    [ObservableProperty]
    private PlazaSortOption? _selectedSort;

    partial void OnSelectedSortChanged(PlazaSortOption? value)
    {
        IsSortLatest = value?.Key == "latest";
        IsSortView = value?.Key == "view";
        IsSortCopy = value?.Key == "copy";
        _ = LoadAsync();
    }

    // ---- 排序 RadioButton 双向绑定（点击任一即更新 SelectedSort） ----

    [ObservableProperty]
    private bool _isSortLatest = true;

    partial void OnIsSortLatestChanged(bool value)
    {
        if (value && SelectedSort?.Key != "latest") SelectedSort = SortItems[0];
    }

    [ObservableProperty]
    private bool _isSortView;

    partial void OnIsSortViewChanged(bool value)
    {
        if (value && SelectedSort?.Key != "view") SelectedSort = SortItems[1];
    }

    [ObservableProperty]
    private bool _isSortCopy;

    partial void OnIsSortCopyChanged(bool value)
    {
        if (value && SelectedSort?.Key != "copy") SelectedSort = SortItems[2];
    }

    /// <summary>我的投稿模式（true=拉取 mySubmits，false=广场列表）。</summary>
    [ObservableProperty]
    private bool _isMySubmits;

    partial void OnIsMySubmitsChanged(bool value)
    {
        OnPropertyChanged(nameof(MySubmitsButtonText));
        _ = LoadAsync();
    }

    [RelayCommand]
    private void ToggleMySubmits()
    {
        IsMySubmits = !IsMySubmits;
    }

    public string MySubmitsButtonText => IsMySubmits
        ? (_local["PromptPlazaBackToPlaza"] ?? "返回广场")
        : (_local["PromptPlazaMySubmits"] ?? "我的投稿");

    // ==================== 瀑布流（Items 直供 WaterfallPanel：动态列数/列宽/最短列分派均由面板处理） ====================

    public ObservableCollection<PlazaItem> Items { get; } = new();

    private int _pageNumber = 1;
    private int _total;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private string _emptyText = "";

    [ObservableProperty]
    private string _countLabel = "";

    [ObservableProperty]
    private bool _isLoadingMore;

    /// <summary>是否还有下一页（Items.Count &lt; 服务端 Total）。</summary>
    public bool HasMore => _total > Items.Count;

    private PlazaItem ToItem(PlazaPrompt p) => new()
    {
        Id = p.Id,
        Title = p.Title,
        Description = p.Description,
        Models = p.Models,
        TagsText = string.IsNullOrWhiteSpace(p.Tags) ? "" : string.Join(" · ", p.Tags.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0)),
        Seed = p.Seed,
        Positive = p.Positive,
        Negative = p.Negative,
        CoverUrl = p.CoverUrl,
        IsRecommended = p.IsRecommended,
        ViewCount = p.ViewCount,
        CopyCount = p.CopyCount,
        AuthorName = p.AuthorName,
        AvatarUrl = p.AuthorAvatarUrl,
    };

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            _pageNumber = 1;
            var page = await FetchPageAsync(_pageNumber);
            var items = page.Items.Select(ToItem).ToList();

            Items.Clear();
            foreach (var item in items) Items.Add(item);

            _total = page.Total;
            OnPropertyChanged(nameof(HasMore));

            HasItems = items.Count > 0;
            CountLabel = string.Format(_local["PromptPlazaCountFormat"] ?? "共 {0} 条", Items.Count, _total);
            EmptyText = _local["PromptPlazaEmpty"] ?? "暂无提示词内容";
            foreach (var item in items)
            {
                _ = LoadCoverAsync(item);
                _ = LoadAvatarAsync(item);
            }
        }
        catch (Exception ex)
        {
            HasItems = false;
            EmptyText = string.Format(_local["PromptPlazaLoadFailedFormat"] ?? "加载失败：{0}", ex.Message);
            LogService.Instance.Warn($"加载提示词广场失败: {ex.Message}", "PromptPlaza", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>滚动到底部加载下一页（追加到 Items 并重新分派；对齐 PromptMaster 无限滚动）。</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || IsLoadingMore || !HasMore) return;
        IsLoadingMore = true;
        try
        {
            var page = await FetchPageAsync(_pageNumber + 1);
            _pageNumber++;
            var items = page.Items.Select(ToItem).ToList();
            foreach (var item in items)
            {
                Items.Add(item);
                _ = LoadCoverAsync(item);
                _ = LoadAvatarAsync(item);
            }
            _total = page.Total;
            OnPropertyChanged(nameof(HasMore));
            // 计数 x 更新为已加载数量（首屏=Items.Count，滚动追加后随 Items 增长）
            CountLabel = string.Format(_local["PromptPlazaCountFormat"] ?? "共 {0} 条", Items.Count, _total);
            LogService.Instance.Info($"提示词广场加载下一页：第 {_pageNumber} 页 +{items.Count} 条，共 {_total} 条", "PromptPlaza");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"提示词广场加载下一页失败: {ex.Message}", "PromptPlaza", ex);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private Task<PlazaPageResult> FetchPageAsync(int pageNumber)
    {
        // "全部"选项 → 请求不传 models（服务端返回全部模型）
        var models = SelectedModel == _allModelLabel ? null : SelectedModel;
        return IsMySubmits
            ? _source.FetchMySubmitsAsync(pageNumber, PageSize, CancellationToken.None)
            : _source.FetchAsync(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                models,
                SelectedSort?.Key ?? "latest",
                pageNumber,
                PageSize,
                CancellationToken.None);
    }

    private async Task LoadCoverAsync(PlazaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.CoverUrl)) return;
        try
        {
            var bytes = await CoverHttp.GetByteArrayAsync(item.CoverUrl);
            item.Cover = new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            // 封面加载失败仅影响展示，不阻断列表
        }
    }

    private async Task LoadAvatarAsync(PlazaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.AvatarUrl)) return;
        try
        {
            var bytes = await CoverHttp.GetByteArrayAsync(item.AvatarUrl);
            item.Avatar = new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            // 头像加载失败仅影响展示（显示首字占位），不阻断列表
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadAsync();
    }

    // ==================== 右侧详情抽屉 ====================

    [ObservableProperty]
    private bool _isDetailOpen;

    [ObservableProperty]
    private bool _isDetailLoading;

    [ObservableProperty]
    private PlazaItem? _detail;

    [ObservableProperty]
    private string _detailStatus = "";

    [RelayCommand]
    private async Task OpenDetailAsync(PlazaItem item)
    {
        if (item == null || IsDetailLoading) return;
        IsDetailOpen = true;
        IsDetailLoading = true;
        DetailStatus = _local["PromptPlazaDetailLoading"] ?? "加载中…";
        Detail = null;
        try
        {
            var d = await _source.FetchDetailAsync(item.Id, CancellationToken.None);
            Detail = new PlazaItem
            {
                Id = d.Id,
                Title = d.Title,
                Description = d.Description,
                Models = d.Models,
                TagsText = string.IsNullOrWhiteSpace(d.Tags) ? "" : string.Join(" · ", d.Tags.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0)),
                Seed = d.Seed,
                Positive = d.Positive,
                Negative = d.Negative,
                CoverUrl = d.CoverUrl,
                IsRecommended = d.IsRecommended,
                ViewCount = d.ViewCount,
                CopyCount = d.CopyCount,
                AuthorName = d.AuthorName,
                AvatarUrl = d.AuthorAvatarUrl,
            };
            _ = LoadCoverAsync(Detail);
            _ = LoadAvatarAsync(Detail);
            DetailStatus = "";
        }
        catch (Exception ex)
        {
            DetailStatus = string.Format(_local["PromptPlazaDetailFailedFormat"] ?? "加载详情失败：{0}", ex.Message);
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    [RelayCommand]
    private void CloseDetail()
    {
        IsDetailOpen = false;
        Detail = null;
        DetailStatus = "";
    }

    [RelayCommand]
    private async Task CopyPositiveAsync()
    {
        if (Detail == null) return;
        await CopyTextAsync(Detail.Positive, "正向提示词", Detail.Id, nameof(Detail.CopyCount));
    }

    [RelayCommand]
    private async Task CopyNegativeAsync()
    {
        if (Detail == null) return;
        await CopyTextAsync(Detail.Negative, "反向提示词", Detail.Id, nameof(Detail.CopyCount));
    }

    private async Task CopyTextAsync(string text, string label, string id, string countProp)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            DetailStatus = _local["PromptPlazaNoPrompt"] ?? $"暂无{label}";
            return;
        }
        try
        {
            _clipboard.CopyToClipboard(text);
            DetailStatus = string.Format(_local["PromptPlazaCopiedFormat"] ?? "已复制{0}", label);
            if (Detail != null) Detail.CopyCount++;
            _ = _source.ReportCopyAsync(id, CancellationToken.None);
            LogService.Instance.Info($"复制提示词广场{label}: {Detail?.Title}", "PromptPlaza");
        }
        catch (Exception ex)
        {
            DetailStatus = ex.Message;
        }
    }
}

/// <summary>广场模型筛选全量列表（对齐 PromptMaster plazaModels：图片组 + 视频组）。</summary>
public static class PlazaModels
{
    public static readonly string[] ImageModels =
    {
        "AbsoluteReality", "AbyssOrangeMix", "AlbedoBase XL", "Animagine XL", "Anima", "Anima aesthetic",
        "Anything V5", "AuraFlow", "ChilloutMix", "Chroma", "Counterfeit", "CyberRealistic",
        "CyberRealistic XL", "DALL-E", "Deliberate", "Doubao", "DreamShaper", "DreamShaper XL",
        "EpicRealism", "Firefly", "Flux.1 Dev", "Flux.1 Kontext", "Flux.1 Pro", "Flux.1 Schnell",
        "Flux2", "Flux1", "GhostMix", "Hassaku", "HiDream", "HunyuanDiT", "Ideogram", "Illustrious",
        "Illustrious XL", "Imagen", "Jimeng", "Juggernaut XL", "Kolors", "Krea", "Krea2", "Leonardo",
        "majicMIX", "MeinaMix", "Midjourney", "NoobAI", "NoobAI XL", "NovelAI", "Nova", "PixArt",
        "Playground", "Pony", "Pony Diffusion V6 XL", "PrefectIllustrious", "Proteus", "Qwen-Image",
        "RavenMix", "RealCartoon", "Realistic Vision", "RealVisXL", "Recraft", "SD 1.5", "SD 3.5",
        "SD 3.5 Large", "SD 3.5 Medium", "SDXL", "SDXL Lightning", "SDXL Turbo", "Seedream",
        "Stable Cascade", "WAI-Illustrious", "Z-Image", "ZavyChromaXL",
    };

    public static readonly string[] VideoModels =
    {
        "AnimateDiff", "CogVideoX", "CogVideoX1.5", "DynamiCrafter", "FramePack", "Hailuo", "HappyHorse",
        "Helios", "HunyuanVideo", "HunyuanVideo 1.5", "I2VGen-XL", "Kling", "Kling 2.6", "Kling 3.0",
        "LTX", "LTX-2", "LTX-2.3", "Luma Dream Machine", "Luma Ray", "Luma Ray3", "Mochi", "Open-Sora",
        "Open-Sora 2.0", "Pika", "Runway Gen-3", "Runway Gen-4", "Runway Gen-4.5", "Seedance",
        "Seedance 1.5", "Seedance 2.0", "SkyReels", "Sora", "Sora 2", "SVD", "Veo", "Veo 2", "Veo 3",
        "Veo 3.1", "Vidu", "Wan2.1", "Wan2.2", "Wan2.7",
    };

    public static IEnumerable<string> All
    {
        get
        {
            foreach (var m in ImageModels) yield return m;
            foreach (var m in VideoModels) yield return m;
        }
    }
}
