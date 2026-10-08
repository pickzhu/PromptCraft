using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.Common;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Service;
using PromptCraft.ViewModels.PromptLibrary;
using Ke.Bee.Localization.Localizer.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 批量保存到词库对话框（对齐 PromptMaster 批量保存：选文件夹 + 标签 + 封面；
/// 标题=文件名、备注=反推自由调用方逐条组装）。宿主为 SukiMessageBox 弹窗（同 PromptEditDialogOpener 范式）。
/// </summary>
public partial class ReverseBatchSaveModel : ObservableObject
{
    private readonly IServiceProvider _service;
    private readonly IPromptLibraryService _library;
    private readonly IWorkspaceService _workspace;
    private readonly ILocalizer _local;
    private readonly IBaseLogService _log;
    private readonly string _batchId = Guid.NewGuid().ToString("N");

    /// <summary>将保存的条数（说明文案）。</summary>
    public int Count { get; }

    public string Intro => string.Format(_local["ReverseBatchIntroFormat"] ?? "", Count);

    // ---- 文件夹（多对多多选；空=未分类） ----

    [ObservableProperty]
    private ObservableCollection<PromptFolderItem> _folders = new();

    [ObservableProperty]
    private List<int> _selectedFolderIds = new();

    /// <summary>文件夹下拉摘要（未选=未分类）。</summary>
    public string SelectedFoldersSummary
        => SelectedFolderIds.Count == 0
            ? (_local["FolderUncategorized"] ?? "")
            : string.Join("、", Folders.Where(f => SelectedFolderIds.Contains(f.Id)).Select(f => f.Name));

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

    // ---- 标签（多选 + 即时新建，对齐 PromptMaster allow-create） ----

    [ObservableProperty]
    private ObservableCollection<PromptTagItem> _tags = new();

    [ObservableProperty]
    private List<string> _selectedTagIds = new();

    [ObservableProperty]
    private string _newTagName = "";

    // ---- 封面 ----

    [ObservableProperty]
    private string _cover = "";

    [ObservableProperty]
    private Bitmap? _coverBitmap;

    [ObservableProperty]
    private bool _isCoverLoading;

    // ---- 状态 ----

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isSaving;

    /// <summary>确认：返回 (folderIds, tagIds, cover)。</summary>
    public event Action<List<int>, List<string>, string>? Confirmed;

    public event Action? Cancelled;

    public ReverseBatchSaveModel(IServiceProvider service, int count)
    {
        _service = service;
        Count = count;
        _library = service.GetRequiredService<IPromptLibraryService>();
        _workspace = service.GetRequiredService<IWorkspaceService>();
        _local = service.GetRequiredService<ILocalizer>();
        _log = service.GetRequiredService<IBaseLogService>();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var folderService = _service.GetRequiredService<IFolderService>();
            var folders = await folderService.GetFoldersAsync(FolderScopes.Prompt);
            Folders = new ObservableCollection<PromptFolderItem>(
                folders.Select(f => new PromptFolderItem(f.Id, f.Name)));

            var tags = await _library.GetTagsAsync();
            Tags = new ObservableCollection<PromptTagItem>(tags.Select(t => new PromptTagItem(t)));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Error(string.Format(_local["ReverseBatchLoadFailedLog"] ?? "", ex.Message), "ReverseBatchSave", ex);
        }
    }

    private async Task LoadCoverAsync()
    {
        var path = Path.Combine(_workspace.CoversDir, Cover);
        if (!File.Exists(path)) return;
        IsCoverLoading = true;
        try
        {
            CoverBitmap = await Task.Run(() =>
            {
                try { using var fs = File.OpenRead(path); return new Bitmap(fs); }
                catch { return null; }
            });
        }
        finally
        {
            IsCoverLoading = false;
        }
    }

    /// <summary>选择封面图片 → 复制到工作空间 covers\&lt;batchId&gt;.&lt;ext&gt;（对齐 importImageFile）。</summary>
    [RelayCommand]
    private async Task PickCoverAsync()
    {
        var provider = StorageService.GetStorageProvider();
        if (provider == null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _local["ReversePickCoverTitle"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(_local["ReverseImageFileType"])
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"],
                },
                StorageService.All,
            ],
        });
        var path = files?.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }.Contains(ext))
            {
                StatusMessage = _local["ReversePickValidImage"];
                return;
            }
            var destName = $"{_batchId}{ext}";
            var dest = Path.Combine(_workspace.CoversDir, destName);
            File.Copy(path, dest, overwrite: true);
            Cover = destName;
            await LoadCoverAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["ReverseSetCoverFailedFormat"] ?? "", ex.Message);
            _log.Error(_local["ReverseSetCoverFailed"], "ReverseBatchSave", ex);
        }
    }

    /// <summary>即时新建全局标签（对齐 PromptMaster allow-create）。</summary>
    [RelayCommand]
    private async Task AddNewTagAsync()
    {
        var name = NewTagName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            StatusMessage = _local["ReverseTagNameRequired"];
            return;
        }
        try
        {
            var tag = await _library.SaveGlobalTagAsync(null, name);
            var item = new PromptTagItem(tag) { IsSelected = true };
            Tags.Add(item);
            var list = SelectedTagIds.ToList();
            list.Add(item.Id);
            SelectedTagIds = list;
            NewTagName = "";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>标签勾选变化 → 同步 SelectedTagIds（Avalonia CheckBox 走 Command，对齐 PromptEditModel）。</summary>
    [RelayCommand]
    private void ToggleTag(PromptTagItem item)
    {
        var list = SelectedTagIds.ToList();
        if (item.IsSelected)
        {
            if (!list.Contains(item.Id)) list.Add(item.Id);
        }
        else
        {
            list.Remove(item.Id);
        }
        SelectedTagIds = list;
    }

    [RelayCommand]
    private void Confirm()
    {
        if (IsSaving) return;
        IsSaving = true;
        try
        {
            Confirmed?.Invoke(SelectedFolderIds.ToList(), SelectedTagIds.ToList(), Cover ?? "");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
