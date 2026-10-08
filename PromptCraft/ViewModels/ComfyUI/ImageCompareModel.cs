using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>字段级差异行（图片 A ｜ 图片 B）</summary>
public sealed class FieldDiffItem
{
    public string Label { get; }
    public string LeftText { get; }
    public string RightText { get; }
    public bool IsDifferent { get; }

    /// <summary>差异行高亮背景（不同=琥珀黄底，相同=透明）</summary>
    public IBrush DiffBackground =>
        IsDifferent ? new SolidColorBrush(Color.Parse("#FFE082")) : Brushes.Transparent;

    /// <summary>参数名颜色：差异行用深棕保证黄底上清晰，相同行用中性灰（不能返回 null，否则文字不渲染）</summary>
    public IBrush LabelBrush =>
        IsDifferent ? new SolidColorBrush(Color.Parse("#5D4300")) : new SolidColorBrush(Color.Parse("#9AA4B2"));

    /// <summary>图A值颜色：差异行用深蓝（黄底可读），相同行用亮蓝</summary>
    public IBrush LeftBrush =>
        IsDifferent ? new SolidColorBrush(Color.Parse("#1F4E79")) : new SolidColorBrush(Color.Parse("#4A90E2"));

    /// <summary>图B值颜色：差异行用深橙（黄底可读），相同行用亮橙</summary>
    public IBrush RightBrush =>
        IsDifferent ? new SolidColorBrush(Color.Parse("#8A4B08")) : new SolidColorBrush(Color.Parse("#E8862E"));

    public FieldDiffItem(string label, string? left, string? right)
    {
        Label = label;
        LeftText = left ?? "—";
        RightText = right ?? "—";
        IsDifferent = !string.Equals(left ?? "", right ?? "", StringComparison.Ordinal)
                      && (left is not null || right is not null);
    }
}

/// <summary>提示词 diff 渲染项（文本 + 归属类型 → 上色）</summary>
public sealed class TextDiffSegmentItem
{
    public string Text { get; }
    public TextDiffKind Kind { get; }

    public IBrush? Background => Kind switch
    {
        TextDiffKind.OnlyLeft => new SolidColorBrush(Color.Parse("#9CC3FF")),
        TextDiffKind.OnlyRight => new SolidColorBrush(Color.Parse("#FFD08A")),
        _ => null,
    };

    public IBrush Foreground => Kind == TextDiffKind.Same ? Brushes.Gray : Brushes.Black;

    public TextDiffSegmentItem(string text, TextDiffKind kind)
    {
        Text = text;
        Kind = kind;
    }
}

/// <summary>工作流节点差异渲染项</summary>
public sealed class NodeDiffItem
{
    public string NodeId { get; }
    public string Title { get; }
    public string ClassType { get; }
    public IReadOnlyList<FieldDiff> Changes { get; }

    public string Summary => string.IsNullOrEmpty(Title) ? string.Format(Localizer.Instance?["NodeSummary"] ?? "", NodeId) : Title;

    public NodeDiffItem(NodeDiff diff)
    {
        NodeId = diff.NodeId;
        Title = diff.Title ?? "";
        ClassType = diff.ClassType;
        Changes = diff.Changes;
    }
}

/// <summary>
/// 图片对比 ViewModel：两张图滑动对比 + 元数据三层差异（参数字段 / 提示词字符级 / 工作流节点级）。
/// 打开时加载限幅 2048 的对比位图（避免双图全解码内存压力）与两图的 Workflow/Prompt 元数据。
/// </summary>
public partial class ImageCompareModel : ViewModelBase
{
    /// <summary>对比画布解码尺寸上限（最长边像素）</summary>
    public const int MaxDecodeDimension = 2048;

    private readonly ImageItem _leftSource;
    private readonly ImageItem _rightSource;
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IBaseLogService _log;

    private ImageInfo? _leftEntity;
    private ImageInfo? _rightEntity;

    [ObservableProperty] private string _leftName = "";
    [ObservableProperty] private string _rightName = "";
    [ObservableProperty] private Bitmap? _leftBitmap;
    [ObservableProperty] private Bitmap? _rightBitmap;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasMetadata;

    // ---- 滑动分割（鼠标左侧显示图 A，右侧显示图 B）----
    [ObservableProperty] private double _canvasWidth = 800;
    [ObservableProperty] private double _canvasHeight = 440;
    [ObservableProperty] private double _splitRatio = 0.5;
    [ObservableProperty] private Rect _leftClipRect = new(0, 0, 400, 440);
    [ObservableProperty] private double _splitLineX = 400;
    [ObservableProperty] private double _splitHandleY = 220;

    // ---- 差异结果 ----
    [ObservableProperty] private bool _workflowSame;
    [ObservableProperty] private string _workflowSummary = "";
    [ObservableProperty] private ObservableCollection<FieldDiffItem> _fieldDiffs = new();
    [ObservableProperty] private ObservableCollection<TextDiffSegmentItem> _positiveSegments = new();
    [ObservableProperty] private ObservableCollection<TextDiffSegmentItem> _negativeSegments = new();
    [ObservableProperty] private ObservableCollection<NodeDiffItem> _nodeDiffs = new();

    /// <summary>两图至少一方有正提示词才显示正提示词对比区（都无则隐藏；相同也照常显示为灰色无高亮）</summary>
    public bool HasPositivePrompt =>
        !string.IsNullOrWhiteSpace(_leftEntity?.Prompt?.PositivePrompt)
        || !string.IsNullOrWhiteSpace(_rightEntity?.Prompt?.PositivePrompt);

    /// <summary>两图至少一方有负提示词才显示负提示词对比区（都无则隐藏；相同也照常显示为灰色无高亮）</summary>
    public bool HasNegativePrompt =>
        !string.IsNullOrWhiteSpace(_leftEntity?.Prompt?.NegativePrompt)
        || !string.IsNullOrWhiteSpace(_rightEntity?.Prompt?.NegativePrompt);

    public string SplitPercent => $"{SplitRatio * 100:0}%";

    public ImageCompareModel(
        ImageItem left,
        ImageItem right,
        ComfySettings settings,
        IDbContextFactory<ComfyDbContext> dbFactory,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log)
        : base(localizer, notice)
    {
        _leftSource = left;
        _rightSource = right;
        _dbFactory = dbFactory;
        _log = log;

        LeftName = left.FileName;
        RightName = right.FileName;
        _ = LoadAsync();
    }

    partial void OnSplitRatioChanged(double value)
    {
        RefreshClip();
        OnPropertyChanged(nameof(SplitPercent));
    }

    /// <summary>View 在画布 SizeChanged 时同步画布尺寸（Clip/分割线的绝对坐标依赖它）</summary>
    public void SetCanvasSize(double width, double height)
    {
        CanvasWidth = width;
        CanvasHeight = height;
        RefreshClip();
    }

    /// <summary>View 在 PointerMoved 时设置分割比例（0~1）</summary>
    public void SetSplit(double ratio) => SplitRatio = Math.Clamp(ratio, 0.0, 1.0);

    public void ResetSplit() => SplitRatio = 0.5;

    private void RefreshClip()
    {
        SplitLineX = CanvasWidth * SplitRatio;
        SplitHandleY = CanvasHeight / 2 - 13;
        LeftClipRect = new Rect(0, 0, SplitLineX, CanvasHeight);
    }

    /// <summary>交换左右图（图片与全部差异结果一起对调）</summary>
    [RelayCommand]
    private void SwapImages()
    {
        (LeftBitmap, RightBitmap) = (RightBitmap, LeftBitmap);
        (LeftName, RightName) = (RightName, LeftName);
        (_leftEntity, _rightEntity) = (_rightEntity, _leftEntity);
        BuildDiffs(_leftEntity, _rightEntity);
    }

    private async Task LoadAsync()
    {
        try
        {
            var bitmaps = await Task.WhenAll(
                Task.Run(() => LoadScaledBitmap(_leftSource.FullPath)),
                Task.Run(() => LoadScaledBitmap(_rightSource.FullPath)));
            LeftBitmap = bitmaps[0];
            RightBitmap = bitmaps[1];

            using var db = await _dbFactory.CreateDbContextAsync();
            _leftEntity = await db.ImageMetadata
                .Include(x => x.Workflow).Include(x => x.Prompt)
                .FirstOrDefaultAsync(x => x.Id == _leftSource.ImageInfoId);
            _rightEntity = await db.ImageMetadata
                .Include(x => x.Workflow).Include(x => x.Prompt)
                .FirstOrDefaultAsync(x => x.Id == _rightSource.ImageInfoId);

            BuildDiffs(_leftEntity, _rightEntity);
        }
        catch (Exception ex)
        {
            _log.Debug(string.Format(_local["CompareLoadFailed"], _leftSource.FileName, _rightSource.FileName), "ImageCompare", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 把原图解码后按最长边 ≤ <see cref="MaxDecodeDimension"/> 缩放（SkiaSharp 绘制缩放，
    /// 与缩略图生成同套路），输出为内存流 PNG 再转 Avalonia Bitmap，避免双图全尺寸解码的内存压力。
    /// </summary>
    private static Bitmap? LoadScaledBitmap(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var input = File.OpenRead(path);
            using var original = SKImage.FromEncodedData(input);
            if (original == null || original.Width <= 0 || original.Height <= 0) return null;

            float scale = Math.Min(1f, (float)MaxDecodeDimension / Math.Max(original.Width, original.Height));
            using var ms = new MemoryStream();
            if (scale < 1f)
            {
                int w = Math.Max(1, (int)(original.Width * scale));
                int h = Math.Max(1, (int)(original.Height * scale));
                var info = new SKImageInfo(w, h);
                using var surface = SKSurface.Create(info);
                if (surface == null) return null;
                surface.Canvas.DrawImage(original, new SKRect(0, 0, w, h));
                using var resized = surface.Snapshot();
                using var data = resized.Encode(SKEncodedImageFormat.Png, 90);
                data.SaveTo(ms);
            }
            else
            {
                using var data = original.Encode(SKEncodedImageFormat.Png, 90);
                data.SaveTo(ms);
            }
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null; // 解码失败画布显示占位，不阻塞对比
        }
    }

    /// <summary>计算三层差异：参数字段 / 正负提示词字符级 diff / 工作流节点级 diff</summary>
    private void BuildDiffs(ImageInfo? left, ImageInfo? right)
    {
        var lp = left?.Prompt;
        var rp = right?.Prompt;
        var lw = left?.Workflow;
        var rw = right?.Workflow;

        HasMetadata = (lp != null || lw != null) || (rp != null || rw != null);

        // ---- 参数层：字段级 ----
        FieldDiffs = new ObservableCollection<FieldDiffItem>(new[]
        {
            new FieldDiffItem(_local["FieldModel"], lp?.Model, rp?.Model),
            new FieldDiffItem("Seed", lp?.Seed, rp?.Seed),
            new FieldDiffItem("Steps", lp?.Steps, rp?.Steps),
            new FieldDiffItem("CFG", lp?.Cfg, rp?.Cfg),
            new FieldDiffItem("Sampler", lp?.Sampler, rp?.Sampler),
            new FieldDiffItem("Scheduler", lp?.Scheduler, rp?.Scheduler),
            new FieldDiffItem(_local["FieldSize"], lp is { Width: > 0 } ? $"{lp.Width}×{lp.Height}" : null,
                rp is { Width: > 0 } ? $"{rp.Width}×{rp.Height}" : null),
            new FieldDiffItem("LoRA", lp?.LoraNames, rp?.LoraNames),
            new FieldDiffItem(_local["FieldWorkflowTitle"], lw?.Name, rw?.Name),
            new FieldDiffItem(_local["FieldNodeCount"], lw is { NodeCount: > 0 } ? lw.NodeCount.ToString() : null,
                rw is { NodeCount: > 0 } ? rw.NodeCount.ToString() : null),
        });

        // ---- 提示词层：字符级 diff ----
        PositiveSegments = ToSegments(TextDiff.Diff(lp?.PositivePrompt, rp?.PositivePrompt));
        NegativeSegments = ToSegments(TextDiff.Diff(lp?.NegativePrompt, rp?.NegativePrompt));
        OnPropertyChanged(nameof(HasPositivePrompt));
        OnPropertyChanged(nameof(HasNegativePrompt));

        // ---- 工作流层：Hash 判定 + 节点级 diff ----
        var wfA = Parse(lw?.GetWorkflowJson());
        var wfB = Parse(rw?.GetWorkflowJson());
        if (wfA == null && wfB == null)
        {
            WorkflowSame = false;
            WorkflowSummary = _local["BothNoMetadata"];
            NodeDiffs = new ObservableCollection<NodeDiffItem>();
        }
        else if (wfA == null || wfB == null)
        {
            WorkflowSame = false;
            WorkflowSummary = wfA == null ? _local["LeftNoMetadata"] : _local["RightNoMetadata"];
            NodeDiffs = new ObservableCollection<NodeDiffItem>();
        }
        else
        {
            WorkflowSame = WorkflowDiffer.SameWorkflow(wfA, wfB);
            WorkflowSummary = WorkflowSame ? _local["SameWorkflowSummary"] : _local["DifferentWorkflowSummary"];
            NodeDiffs = new ObservableCollection<NodeDiffItem>(
                WorkflowDiffer.Diff(wfA, wfB).Select(n => new NodeDiffItem(n)));
        }
    }

    private static ObservableCollection<TextDiffSegmentItem> ToSegments(List<TextDiffSegment> segs) =>
        new(segs.Select(s => new TextDiffSegmentItem(s.Text, s.Kind)));

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
