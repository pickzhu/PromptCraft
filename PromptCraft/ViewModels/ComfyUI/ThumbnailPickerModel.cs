using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Models.ComfyUI;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>图库图片选择项（封面选择器用）。</summary>
public partial class ThumbnailPickerItem : ObservableObject
{
    public int Id { get; init; }
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public List<string> Tags { get; init; } = new();

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;

    /// <summary>瀑布流显示高度：按图片宽高比折算（卡片宽 112），clamp 56~168，默认 92。</summary>
    [ObservableProperty] private double _thumbHeight = 92;
}

/// <summary>标签筛选下拉选项。</summary>
public class TagOption
{
    public string? Value { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>
/// 图库封面选择器 ViewModel：
/// 瀑布流（按比例高度、贪心分 4 列）、默认显示前 50 张、文件名搜索 + Tags 筛选、加载更多；
/// 结果通过 <see cref="Result"/> 返回：PickedGallery（SelectedPath）/ PickFromComputer / Cancelled。
/// </summary>
public partial class ThumbnailPickerModel : ViewModelBase
{
    public enum PickerResult
    {
        Cancelled,
        PickedGallery,
        PickFromComputer,
    }

    private const int ColumnCount = 4;
    private const int PageSize = 50;

    private readonly ComfySettings _settings;
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly PromptCraft.Interfaces.IBaseLogService _log;

    /// <summary>仅显示这些 ImageInfo Id 的图片（null=全部；工作流封面选择/选用图片参数时传入该工作流产物集合）。</summary>
    private readonly IReadOnlyCollection<int>? _allowedImageIds;

    /// <summary>加载完成后自动选中第一张（选用图片参数场景：默认最新一张）。</summary>
    private readonly bool _autoSelectFirst;

    private List<ThumbnailPickerItem> _allItems = new();
    private int _visibleCount;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ObservableCollection<ObservableCollection<ThumbnailPickerItem>> _columns = new();
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private TagOption? _selectedTagOption;
    [ObservableProperty] private ObservableCollection<TagOption> _tagOptions = new();
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _statusText = "";

    /// <summary>弹窗关闭后的结果（默认取消）。</summary>
    public PickerResult Result { get; private set; } = PickerResult.Cancelled;

    /// <summary>选中图片的绝对路径（Result == PickedGallery 时有效）。</summary>
    public string? SelectedPath => SelectedItem?.FullPath;

    /// <summary>选中图片的 ImageInfo Id（选用图片参数场景：按 Id 查该图节点参数）。</summary>
    public int? SelectedImageInfoId => SelectedItem?.Id;

    /// <summary>当前选中项（属性变更用于驱动弹窗"确定"按钮可用性）。</summary>
    public ThumbnailPickerItem? SelectedItem { get; private set; }

    /// <summary>请求关闭弹窗（由打开方订阅并关闭窗口）。</summary>
    public event Action? RequestClose;

    public ThumbnailPickerModel(
        ComfySettings settings,
        IDbContextFactory<ComfyDbContext> dbFactory,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log,
        IReadOnlyCollection<int>? allowedImageIds = null,
        bool autoSelectFirst = false)
        : base(localizer, notice)
    {
        _settings = settings;
        _dbFactory = dbFactory;
        _log = log;
        _allowedImageIds = allowedImageIds;
        _autoSelectFirst = autoSelectFirst;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTagOptionChanged(TagOption? value) => ApplyFilter();

    /// <summary>加载图库全部图片（时间倒序，含标签）后应用搜索/筛选并渲染首屏。</summary>
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            IQueryable<ImageInfo> query = db.ImageMetadata.AsNoTracking()
                .Include(x => x.ImageTags).ThenInclude(x => x.Tag);
            if (_allowedImageIds != null)
                query = query.Where(x => _allowedImageIds.Contains(x.Id));
            var imgs = await query
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            _allItems = imgs.Select(x =>
            {
                var rel = x.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                return new ThumbnailPickerItem
                {
                    Id = x.Id,
                    FileName = x.FileName,
                    FullPath = string.IsNullOrEmpty(_settings.ComfyOutputDir) ? "" : Path.Combine(_settings.ComfyOutputDir, rel),
                    Tags = x.ImageTags.Select(t => t.Tag.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct()
                        .ToList(),
                };
            }).ToList();

            // 标签筛选项：与图库页一致，直接列出 Tags 表全部标签（首项"全部标签" Value=null）
            var tags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
            TagOptions = new ObservableCollection<TagOption>(
                new[] { new TagOption { Value = null, Text = _local["AllTags"] } }
                .Concat(tags.Select(t => new TagOption { Value = t.Name, Text = t.Name })));

            ApplyFilter();

            // 自动选中第一张（过滤后最新一张；选用图片参数场景默认即可直接确定）
            if (_autoSelectFirst && _allItems.Count > 0)
            {
                var first = FilterAll().FirstOrDefault();
                if (first != null) SelectItem(first);
            }

            _log.Info(string.Format(_local["ThumbPickerLoadGallery"], _allItems.Count), "Workflow");
        }
        catch (Exception ex)
        {
            StatusText = string.Format(_local["LoadImagesFailed"], ex.Message);
            _log.Error(_local["LoadThumbPickerImagesFailed"], "Workflow", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>按搜索词 + 标签过滤（含分页：默认前 50）。</summary>
    private void ApplyFilter()
    {
        var list = FilterAll();
        _visibleCount = PageSize;
        HasMore = list.Count > _visibleCount;
        StatusText = string.Format(_local["TotalImagesCount"], list.Count);
        RebuildColumns(list.Take(_visibleCount).ToList());
    }

    private List<ThumbnailPickerItem> FilterAll()
    {
        IEnumerable<ThumbnailPickerItem> q = _allItems;
        var kw = SearchText?.Trim();
        if (!string.IsNullOrEmpty(kw))
            q = q.Where(x => x.FileName.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var tag = SelectedTagOption?.Value;
        if (!string.IsNullOrEmpty(tag))
            q = q.Where(x => x.Tags.Contains(tag));
        return q.ToList();
    }

    /// <summary>加载更多（每次 +50）。</summary>
    [RelayCommand]
    private void LoadMore()
    {
        if (!HasMore) return;
        var list = FilterAll();
        _visibleCount += PageSize;
        HasMore = list.Count > _visibleCount;
        RebuildColumns(list.Take(_visibleCount).ToList());
    }

    /// <summary>瀑布流分 4 列：新卡片加入当前总高最矮的列（按折算高度，形成错落）。</summary>
    private void RebuildColumns(List<ThumbnailPickerItem> items)
    {
        var cols = new ObservableCollection<ObservableCollection<ThumbnailPickerItem>>();
        for (var i = 0; i < ColumnCount; i++)
            cols.Add(new ObservableCollection<ThumbnailPickerItem>());

        var heights = new double[ColumnCount];
        foreach (var item in items)
        {
            var col = Array.IndexOf(heights, heights.Min());
            cols[col].Add(item);
            heights[col] += item.ThumbHeight + 24;
        }
        Columns = cols;

        _ = LoadVisibleThumbsAsync(items);
    }

    /// <summary>可见项异步加载缩略图（并行解码，回 UI 线程赋值并折算瀑布流高度）。</summary>
    private async Task LoadVisibleThumbsAsync(List<ThumbnailPickerItem> items)
    {
        try
        {
            var pending = items.Where(x => x.Thumbnail == null).ToList();
            if (pending.Count == 0) return;
            var loaded = await Task.WhenAll(pending.Select(async it => (it, await Task.Run(() => LoadThumb(it)))));
            foreach (var (item, bmp) in loaded)
            {
                if (bmp == null) continue;
                item.Thumbnail = bmp;
                var ratio = bmp.PixelSize.Width / (double)bmp.PixelSize.Height;
                if (ratio is > 0.1 and < 10)
                    item.ThumbHeight = Math.Clamp(112.0 / ratio, 56, 168);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(_local["ThumbLoadThumbFailed"], "Workflow", ex);
        }
    }

    private Bitmap? LoadThumb(ThumbnailPickerItem item)
    {
        if (string.IsNullOrEmpty(item.FullPath)) return null;
        var thumbFile = $"{Path.GetFileNameWithoutExtension(item.FileName)}_{_settings.ThumbMaxDimension}.jpg";
        var thumbPath = Path.Combine(_settings.ThumbDir, thumbFile);
        var path = File.Exists(thumbPath) ? thumbPath : item.FullPath;
        if (!File.Exists(path)) return null;
        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            _log.Debug(string.Format(_local["ThumbDecodeFailed"], path), "Workflow", ex);
            return null;
        }
    }

    /// <summary>单击选中（列表卡片 PointerPressed 调用）。</summary>
    [RelayCommand]
    private void SelectItem(ThumbnailPickerItem? item)
    {
        if (item == null) return;
        if (SelectedItem != null) SelectedItem.IsSelected = false;
        item.IsSelected = true;
        SelectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
    }

    /// <summary>确定：使用选中图库图片作为封面。</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (SelectedItem == null) return;
        Result = PickerResult.PickedGallery;
        RequestClose?.Invoke();
    }

    /// <summary>从电脑选择图片（关闭弹窗后由调用方走文件选择流程）。</summary>
    [RelayCommand]
    private void PickFromComputer()
    {
        Result = PickerResult.PickFromComputer;
        RequestClose?.Invoke();
    }
}