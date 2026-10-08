using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.PromptLibrary;

/// <summary>词库标签抽屉条目（名称 + 颜色 + 使用量；颜色来自全局标签池）。</summary>
public partial class PromptTagEditItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int UsageCount { get; set; }
    public IBrush Color { get; set; } = Brushes.Gray;
}

/// <summary>
/// 词库标签管理抽屉 ViewModel：数据走**全局标签池**（<see cref="Tag"/>，与图库/工作流共用同一套标签，
/// 在任意一处新建/重命名/删除立即全局生效）。新建/重命名/删除（确认）语义与图库
/// TagManagerDrawerModel 一致（同名拒绝、调色板取色、删除级联清理关联）。
/// </summary>
public partial class PromptLibraryTagDrawerModel : ViewModelBase
{
    private readonly IPromptLibraryService _library;
    private readonly IDbContextFactory<ComfyDbContext> _globalDbFactory;
    private readonly IBaseLogService _log;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private ObservableCollection<PromptTagEditItem> _tags = new();
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _newTagCounter = "0/40";
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>标签数据变更（新建/重命名/删除）后触发，宿主订阅后刷新。</summary>
    public event Action? TagsChanged;

    public PromptLibraryTagDrawerModel(
        IServiceProvider service,
        IPromptLibraryService library,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        IBaseLogService log)
        : base(localizer, notice)
    {
        _library = library;
        _log = log;
        _globalDbFactory = service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
    }

    partial void OnNewTagNameChanged(string value)
    {
        NewTagCounter = $"{value?.Length ?? 0}/40";
    }

    public async Task OpenAsync()
    {
        await LoadAsync();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        TagsChanged?.Invoke();
    }

    /// <summary>加载全局标签池（使用量 = 图片关联 + 工作流关联，与图库抽屉口径一致）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            using var db = await _globalDbFactory.CreateDbContextAsync();
            var tags = await db.Tags
                .Select(t => new
                {
                    t.Id,
                    t.Name,
                    t.Color,
                    UsageCount = (t.ImageTags == null ? 0 : t.ImageTags.Count) + (t.WorkflowTags == null ? 0 : t.WorkflowTags.Count),
                })
                .OrderBy(x => x.Name)
                .ToListAsync();
            Tags = new ObservableCollection<PromptTagEditItem>(tags.Select(t => new PromptTagEditItem
            {
                Id = t.Id.ToString(),
                Name = t.Name,
                UsageCount = t.UsageCount,
                Color = TagPalette.GetBrush(t.Color, t.Name),
            }));
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptTagsLoadFailed"], ex.Message);
            _log.Error(_local["PromptTagDrawerLoadFailed"], "PromptLibrary", ex);
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = NewTagName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.Length > 40) { StatusMessage = _local["PromptTagNameTooLong"]; return; }

        try
        {
            var tag = await _library.SaveGlobalTagAsync(null, name);
            NewTagName = "";
            StatusMessage = string.Format(_local["PromptTagCreatedWithName"], tag.Name);
            await LoadAsync();
            TagsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task EditTagAsync(PromptTagEditItem? item)
    {
        if (item == null) return;

        var tb = new TextBox
        {
            Text = item.Name,
            PlaceholderText = _local["PromptTagName"],
            MinWidth = 260,
            Margin = new Thickness(0, 8, 0, 8),
        };
        var result = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = tb,
                ActionButtonsPreset = SukiMessageBoxButtons.OK,
            },
            new SukiMessageBoxOptions { Title = _local["PromptRenameTag"], MinWidth = 340 });
        if (result is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;

        var newName = tb.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        if (newName.Length > 40) { StatusMessage = _local["PromptTagNameTooLong"]; return; }

        try
        {
            await _library.SaveGlobalTagAsync(item.Id, newName);
            StatusMessage = string.Format(_local["PromptTagRenamed"], newName);
            await LoadAsync();
            TagsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteTagAsync(PromptTagEditItem? item)
    {
        if (item == null) return;

        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = string.Format(_local["PromptTagDeleteConfirm"], item.Name),
                    Margin = new Thickness(4),
                    TextWrapping = TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["ConfirmDelete"], MinWidth = 320 });
        if (confirm is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;

        try
        {
            await _library.DeleteGlobalTagAsync(item.Id);
            StatusMessage = string.Format(_local["PromptTagDeletedWithName"], item.Name);
            await LoadAsync();
            TagsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptDeleteFailed"], ex.Message);
            _log.Error(string.Format(_local["PromptTagDeleteFailedLog"], item.Name), "PromptLibrary", ex);
        }
    }
}
