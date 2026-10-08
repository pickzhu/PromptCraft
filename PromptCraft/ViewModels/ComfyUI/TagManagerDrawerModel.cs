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
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>
/// 标签管理抽屉组件 ViewModel（图库 / 工作流页共用）。
/// 负责抽屉的开关、标签列表加载、新建 / 重命名 / 删除（含确认对话框），
/// 所有写操作统一从 <see cref="TagPalette"/> 生成颜色并保存到 Tags.Color。
/// 数据变更后通过 <see cref="TagsChanged"/> 通知宿主刷新标签下拉与列表。
/// </summary>
public partial class TagManagerDrawerModel : ViewModelBase
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly ITagRepository _tagRepository;
    private readonly IBaseLogService _log;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private ObservableCollection<TagEditItem> _tags = new();
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _newTagCounter = "0/40";
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>标签数据变更（新建/重命名/删除）后触发，宿主订阅后刷新自己的标签下拉与列表。</summary>
    public event Action? TagsChanged;

    public TagManagerDrawerModel(
        IDbContextFactory<ComfyDbContext> dbFactory,
        ITagRepository tagRepository,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log)
        : base(localizer, notice)
    {
        _dbFactory = dbFactory;
        _tagRepository = tagRepository;
        _log = log;
    }

    partial void OnNewTagNameChanged(string value)
    {
        NewTagCounter = $"{value?.Length ?? 0}/40";
    }

    /// <summary>打开抽屉并加载标签列表。</summary>
    public async Task OpenAsync()
    {
        await LoadAsync();
        IsOpen = true;
    }

    /// <summary>关闭抽屉，并通知宿主刷新（标签可能已变更）。</summary>
    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        TagsChanged?.Invoke();
    }

    /// <summary>加载全部标签（使用量 = 图片关联 + 工作流关联，标签池共用）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
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
            Tags = new ObservableCollection<TagEditItem>(tags.Select(t => new TagEditItem
            {
                Id = t.Id,
                Name = t.Name,
                UsageCount = t.UsageCount,
                Color = TagPalette.GetBrush(t.Color, t.Name),
            }));
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["LoadTagsFailedDetail"], ex.Message);
            _log.Error(_local["LoadTagDrawerFailed"], "TagManager", ex);
        }
    }

    /// <summary>新建标签：从调色板随机取色（避开已用颜色）并保存。</summary>
    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = NewTagName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.Length > 40) { StatusMessage = _local["TagNameTooLong40"]; return; }

        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            if (await db.Tags.AnyAsync(t => t.Name == name))
            {
                StatusMessage = string.Format(_local["TagNameExists"], name);
                return;
            }

            var used = TagPalette.NormalizeUsedColors(
                await db.Tags.Where(t => t.Color != null).Select(t => t.Color).ToListAsync());
            var tag = new Tag { Name = name, Color = TagPalette.PickDistinctColorHex(used) };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            _log.Info(string.Format(_local["TagCreatedLog"], name, tag.Color), "TagManager");

            NewTagName = "";
            StatusMessage = string.Format(_local["TagCreated"], name);
            await LoadAsync();
            TagsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["CreateTagFailedDetail"], ex.Message);
            _log.Error(string.Format(_local["CreateTagFailed"], name), "TagManager", ex);
        }
    }

    /// <summary>重命名标签（弹输入框，校验重名）。</summary>
    [RelayCommand]
    private async Task EditTagAsync(TagEditItem? item)
    {
        if (item == null) return;

        var tb = new TextBox
        {
            Text = item.Name,
            PlaceholderText = _local["TagName"],
            MinWidth = 260,
            Margin = new Thickness(0, 8, 0, 8),
        };
        var result = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = tb,
                ActionButtonsPreset = SukiMessageBoxButtons.OK,
            },
            new SukiMessageBoxOptions
            {
                Title = _local["RenameTagTitle"],
                MinWidth = 340,
            });
        if (result is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;

        var newName = tb.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        if (newName.Length > 40) { StatusMessage = _local["TagNameTooLong40"]; return; }

        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == item.Id);
            if (tag == null) return;
            if (await db.Tags.AnyAsync(t => t.Name == newName && t.Id != item.Id))
            {
                StatusMessage = string.Format(_local["TagNameExists"], newName);
                return;
            }
            tag.Name = newName;
            await db.SaveChangesAsync();
            _log.Info(string.Format(_local["RenameTagLog"], item.Name, newName), "TagManager");

            StatusMessage = string.Format(_local["TagRenamed"], newName);
            await LoadAsync();
            TagsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["RenameFailedDetail"], ex.Message);
            _log.Error(string.Format(_local["RenameTagFailed"], item.Name), "TagManager", ex);
        }
    }

    /// <summary>删除标签（确认后联动清理图片与工作流关联，标签池共用）。</summary>
    [RelayCommand]
    private async Task DeleteTagAsync(TagEditItem? item)
    {
        if (item == null) return;

        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = string.Format(_local["ConfirmDeleteTagFull"], item.Name),
                    Margin = new Thickness(4),
                    TextWrapping = TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions
            {
                Title = _local["ConfirmDelete"],
                MinWidth = 320,
            });
        if (confirm is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;

        try
        {
            await _tagRepository.DeleteAsync(item.Id);
            _log.Info(string.Format(_local["DeleteTagLog"], item.Name), "TagManager");

            StatusMessage = string.Format(_local["TagDeleted"], item.Name);
            await LoadAsync();
            TagsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["DeleteFailedDetail"], ex.Message);
            _log.Error(string.Format(_local["DeleteTagFailed"], item.Name), "TagManager", ex);
        }
    }
}
