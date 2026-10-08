using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.PromptLibrary;

/// <summary>词库标签项（全局标签池；编辑对话框 / 多选筛选共用；IsSelected 仅筛选下拉使用）。</summary>
public partial class PromptTagItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public IBrush Color { get; set; } = Brushes.Gray;

    [ObservableProperty] private bool _isSelected;

    public PromptTagItem() { }

    /// <summary>以全局标签（Tag）构造：Id 取字符串形式，颜色用持久化 Color（缺省回退名称哈希）。</summary>
    public PromptTagItem(Tag tag)
    {
        Id = tag.Id.ToString();
        Name = tag.Name;
        Color = TagPalette.GetBrush(tag.Color, tag.Name);
    }
}

/// <summary>词库文件夹项（多选下拉 / 筛选共用；IsSelected 仅在多选场景使用）。</summary>
public partial class PromptFolderItem : ObservableObject
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [ObservableProperty] private bool _isSelected;

    public PromptFolderItem() { }

    public PromptFolderItem(int id, string name)
    {
        Id = id;
        Name = name;
    }
}

/// <summary>卡片上展示的标签 chip（名称 + 颜色）。</summary>
public partial class PromptTagChip : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public IBrush Color { get; set; } = Brushes.Gray;
    public IBrush Border { get; set; } = Brushes.Transparent;
}

/// <summary>
/// 词库卡片项（对齐 PmLibrary 卡片：封面 / 标题 / 更新时间 / 标签行）。
/// 封面按需异步加载（工作空间 covers 目录），加载失败显示占位。
/// </summary>
public partial class PromptCardItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Positive { get; set; } = "";
    public string Negative { get; set; } = "";
    public string Note { get; set; } = "";
    public string Cover { get; set; } = "";
    /// <summary>所属文件夹 Id 集合（多对多；空=未分类）。</summary>
    public List<int> FolderIds { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
    public string UpdatedAtDisplay { get; set; } = "";
    public List<string> TagIds { get; set; } = new();
    public ObservableCollection<PromptTagChip> Tags { get; set; } = new();

    /// <summary>封面完整路径（工作空间 covers\cover 相对路径）。</summary>
    public string? CoverFullPath { get; set; }

    [ObservableProperty] private Bitmap? _coverBitmap;
    [ObservableProperty] private bool _isCoverLoading;
    [ObservableProperty] private bool _isSelected;

    public bool HasCover => !string.IsNullOrEmpty(Cover);

    /// <summary>异步加载封面位图（UI 线程外解码，避免卡界面）。</summary>
    public async Task LoadCoverAsync()
    {
        if (string.IsNullOrEmpty(CoverFullPath) || !File.Exists(CoverFullPath))
        {
            CoverBitmap = null;
            return;
        }

        IsCoverLoading = true;
        try
        {
            var path = CoverFullPath;
            var bitmap = await Task.Run(() =>
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    return new Bitmap(fs);
                }
                catch
                {
                    return null;
                }
            });
            CoverBitmap = bitmap;
        }
        finally
        {
            IsCoverLoading = false;
        }
    }
}
