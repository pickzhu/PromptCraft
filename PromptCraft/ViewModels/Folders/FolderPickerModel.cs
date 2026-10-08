using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.Interfaces;
using PromptCraft.ViewModels.PromptLibrary;
using Ke.Bee.Localization.Localizer.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.Folders;

/// <summary>
/// 加入文件夹弹窗 Model（提示词/工作流/图库三页共用）：列出全部文件夹（文件夹不再分资产类型），多选后保存。
/// 宿主为 SukiMessageBox 弹窗，foot 提供 [取消][保存]（同 ReverseBatchSave 范式）。
/// </summary>
public partial class FolderPickerModel : ObservableObject
{
    private readonly ILocalizer _local;
    private readonly IBaseLogService _log;
    private readonly IFolderService _folderService;
    private readonly string _scope;

    [ObservableProperty] private ObservableCollection<PromptFolderItem> _folders = new();
    [ObservableProperty] private List<int> _selectedFolderIds = new();
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>下拉摘要（未选=未分类）。</summary>
    public string SelectedFoldersSummary
        => SelectedFolderIds.Count == 0
            ? (_local["FolderUncategorized"] ?? "")
            : string.Join("、", Folders.Where(f => SelectedFolderIds.Contains(f.Id)).Select(f => f.Name));

    /// <summary>确认：返回所选文件夹 Id 集合（空=未选择任何文件夹）。</summary>
    public event Action<List<int>>? Confirmed;

    /// <summary>取消。</summary>
    public event Action? Cancelled;

    public FolderPickerModel(IServiceProvider service, string scope, IReadOnlyList<int>? preSelected = null)
    {
        _scope = scope;
        _local = service.GetRequiredService<ILocalizer>();
        _log = service.GetRequiredService<IBaseLogService>();
        _folderService = service.GetRequiredService<IFolderService>();
        _ = LoadFoldersAsync(preSelected);
    }

    private async Task LoadFoldersAsync(IReadOnlyList<int>? preSelected)
    {
        try
        {
            var folders = await _folderService.GetAllFoldersAsync();
            Folders = new ObservableCollection<PromptFolderItem>(
                folders.Select(f => new PromptFolderItem(f.Id, f.Name)));

            var set = (preSelected ?? Array.Empty<int>()).ToHashSet();
            foreach (var f in Folders) f.IsSelected = set.Contains(f.Id);
            SelectedFolderIds = set.ToList();
            OnPropertyChanged(nameof(SelectedFoldersSummary));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Error(string.Format(_local["FolderSaveFailed"] ?? "", ex.Message), "FolderPicker", ex);
        }
    }

    partial void OnSelectedFolderIdsChanged(List<int> value)
    {
        var set = value.ToHashSet();
        foreach (var f in Folders) f.IsSelected = set.Contains(f.Id);
        OnPropertyChanged(nameof(SelectedFoldersSummary));
    }

    /// <summary>文件夹勾选变化 → 同步 SelectedFolderIds。</summary>
    [RelayCommand]
    private void ToggleFolder(PromptFolderItem item)
    {
        var list = SelectedFolderIds.ToList();
        if (item.IsSelected)
        {
            if (!list.Contains(item.Id)) list.Add(item.Id);
        }
        else
        {
            list.Remove(item.Id);
        }
        SelectedFolderIds = list;
    }

    [RelayCommand]
    private void Confirm()
    {
        try
        {
            Confirmed?.Invoke(SelectedFolderIds.ToList());
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
