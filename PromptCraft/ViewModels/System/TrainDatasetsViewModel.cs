using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.Service;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>数据集列表行。</summary>
public partial class TrainDatasetRow : ObservableObject
{
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string MaterialMode { get; set; } = "managed";
    public bool IsScanPath => MaterialMode == "scan_path";
    public int ImageCount { get; set; }
    public int VideoCount { get; set; }
    public int TotalCount => ImageCount + VideoCount;
    public string ModeLabel { get; set; } = "";
}

/// <summary>
/// 模型训练打标页（侧边栏入口，对齐 PromptMaster TrainDatasets）：
/// 数据集列表 + 新建/重命名/删除/打开文件夹；点击进入 <see cref="TrainDatasetDetailViewModel"/>。
/// 内嵌详情子页（列表/详情切换），无需额外导航。
/// </summary>
public partial class TrainDatasetsViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private readonly IServiceProvider _services;
    private readonly DatasetService _ds;

    public TrainDatasetsViewModel(ILocalizer localizer, IBaseNotice baseNotice, IServiceProvider services)
        : base(localizer, baseNotice)
    {
        _displayName = "TRAINDATASETS";
        _icon = MaterialIconKind.Database;
        _index = 60;
        _sideMenu = true;
        _services = services;
        _ds = services.GetRequiredService<DatasetService>();
        _ = LoadAsync();
    }

    public string Title => _local["TrainDatasetsTitle"] ?? "";

    // ==================== 列表 ====================

    public ObservableCollection<TrainDatasetRow> Rows { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value) => _ = LoadAsync();

    [ObservableProperty]
    private string _countLabel = "";

    [ObservableProperty]
    private string _statusMessage = "";

    private async Task LoadAsync()
    {
        var items = await Task.Run(() => _ds.List(string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim()));
        Rows.Clear();
        foreach (var it in items)
        {
            Rows.Add(new TrainDatasetRow
            {
                Name = it.Name,
                LocalPath = it.LocalPath,
                MaterialMode = it.MaterialMode,
                ImageCount = it.ImageCount,
                VideoCount = it.VideoCount,
                ModeLabel = it.MaterialMode == "scan_path"
                    ? (_local["TrainModeScan"] ?? "目录扫描")
                    : (_local["TrainModeManaged"] ?? "素材库"),
            });
        }
        CountLabel = string.Format(_local["TrainCountFormat"] ?? "共 {0} 个数据集", Rows.Count);
    }

    // ==================== 详情子页 ====================

    [ObservableProperty]
    private bool _isDetailView;

    /// <summary>当前详情子页 VM。必须用 ObservableProperty 通知 ContentControl 刷新内容，
    /// 否则进入数据集后 Content 不更新 → 详情页空白且看不到返回按钮。</summary>
    [ObservableProperty]
    private TrainDatasetDetailViewModel? _detail;

    [RelayCommand]
    private void OpenDataset(TrainDatasetRow row)
    {
        if (row == null) return;
        var detail = new TrainDatasetDetailViewModel(_local, _noticeService, _services, row.Name);
        detail.BackRequested += OnDetailBack;
        Detail = detail;
        IsDetailView = true;
    }

    private void OnDetailBack()
    {
        if (Detail != null) Detail.BackRequested -= OnDetailBack;
        Detail = null;
        IsDetailView = false;
        _ = LoadAsync();
    }

    // ==================== 操作 ====================

    [RelayCommand]
    private async Task NewAsync()
    {
        var tb = new TextBox
        {
            PlaceholderText = _local["TrainNewHint"] ?? "请输入数据集名称",
            MinWidth = 300,
            Margin = new Thickness(0, 8, 0, 8),
        };
        var result = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost { Content = tb, ActionButtonsPreset = SukiMessageBoxButtons.OKCancel },
            new SukiMessageBoxOptions { Title = _local["TrainNewTitle"] ?? "新建数据集", MinWidth = 380 });
        if (result is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;
        var name = (tb.Text ?? "").Trim();
        if (name.Length == 0) return;
        var (ok, msg) = _ds.Add(name);
        if (ok)
        {
            StatusMessage = string.Format(_local["TrainCreatedFormat"] ?? "数据集 {0} 已创建", name);
            LogService.Instance.Info($"新建数据集: {name}", "TrainDataset");
            await LoadAsync();
        }
        else
        {
            StatusMessage = msg;
        }
    }

    [RelayCommand]
    private async Task RenameAsync(TrainDatasetRow row)
    {
        if (row == null) return;
        var tb = new TextBox
        {
            Text = row.Name,
            PlaceholderText = _local["TrainRenameHint"] ?? "请输入新名称",
            MinWidth = 300,
            Margin = new Thickness(0, 8, 0, 8),
        };
        var result = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost { Content = tb, ActionButtonsPreset = SukiMessageBoxButtons.OKCancel },
            new SukiMessageBoxOptions { Title = _local["TrainRenameTitle"] ?? "重命名数据集", MinWidth = 380 });
        if (result is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;
        var name = (tb.Text ?? "").Trim();
        if (name.Length == 0 || name == row.Name) return;
        var (ok, msg) = _ds.Rename(row.Name, name);
        if (ok)
        {
            StatusMessage = string.Format(_local["TrainRenamedFormat"] ?? "已重命名为 {0}", name);
            LogService.Instance.Info($"重命名数据集: {row.Name} → {name}", "TrainDataset");
            await LoadAsync();
        }
        else
        {
            StatusMessage = msg;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(TrainDatasetRow row)
    {
        if (row == null) return;
        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = string.Format(_local["TrainDeleteConfirmFormat"] ?? "确定删除数据集「{0}」？此操作会连同其中的素材与打标文件一并删除，且不可恢复。", row.Name),
                    Margin = new Thickness(4),
                    TextWrapping = TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["TrainDeleteTitle"] ?? "删除数据集", MinWidth = 340 });
        if (confirm is not SukiMessageBoxResult c || !c.Equals(SukiMessageBoxResult.OK)) return;
        var (ok, msg) = _ds.Delete(row.Name);
        if (ok)
        {
            StatusMessage = string.Format(_local["TrainDeletedFormat"] ?? "已删除数据集 {0}", row.Name);
            LogService.Instance.Info($"删除数据集: {row.Name}", "TrainDataset");
            await LoadAsync();
        }
        else
        {
            StatusMessage = msg;
        }
    }

    [RelayCommand]
    private void OpenFolder(TrainDatasetRow row)
    {
        if (row == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.LocalPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
