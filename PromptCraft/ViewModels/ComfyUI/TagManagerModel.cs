using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>标签管理对话框 ViewModel</summary>
public partial class TagManagerModel : ViewModelBase
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IBaseLogService _log;

    [ObservableProperty] private ObservableCollection<TagEditItem> _tags = new();
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _statusMessage = "";

    public TagManagerModel(
        IDbContextFactory<ComfyDbContext> dbFactory,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log)
        : base(localizer, notice)
    {
        _dbFactory = dbFactory;
        _log = log;
        _ = LoadTagsAsync();
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var tags = await db.Tags.OrderBy(t => t.Name).ToListAsync();
            Tags = new ObservableCollection<TagEditItem>(tags.Select(t => new TagEditItem
            {
                Id = t.Id,
                Name = t.Name,
                UsageCount = t.ImageTags?.Count ?? 0,
                Color = TagPalette.GetBrush(t.Color, t.Name),
            }));
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["LoadFailed"], ex.Message);
            _log.Error(_local["LoadTagListFailed"], "TagManager", ex);
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = NewTagName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.Length > 128) { StatusMessage = _local["TagNameTooLong128"]; return; }

        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var exists = await db.Tags.AnyAsync(t => t.Name == name);
            if (exists) { StatusMessage = string.Format(_local["TagNameExists"], name); return; }

            var used = TagPalette.NormalizeUsedColors(
                await db.Tags.Where(t => t.Color != null).Select(t => t.Color).ToListAsync());
            var tag = new Tag { Name = name, Color = TagPalette.PickDistinctColorHex(used) };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            _log.Info(string.Format(_local["CreateTagLog"], name), "TagManager");

            Tags.Add(new TagEditItem
            {
                Id = tag.Id,
                Name = tag.Name,
                Color = TagPalette.GetBrush(tag.Color, tag.Name),
                UsageCount = 0,
                IsNew = true
            });
            NewTagName = "";
            StatusMessage = string.Format(_local["TagCreated"], name);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["ErrorBadge"], ex.Message);
            _log.Error(string.Format(_local["CreateTagFailed"], name), "TagManager", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteTagAsync(TagEditItem? item)
    {
        if (item == null) return;
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var tag = await db.Tags
                .Include(t => t.ImageTags)
                .FirstOrDefaultAsync(t => t.Id == item.Id);
            if (tag == null) return;

            // 先删除关联
            db.ImageTags.RemoveRange(tag.ImageTags);
            db.Tags.Remove(tag);
            await db.SaveChangesAsync();
            _log.Info(string.Format(_local["DeleteTagLog"], item.Name), "TagManager");

            Tags.Remove(item);
            StatusMessage = string.Format(_local["TagDeleted"], item.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["ErrorBadge"], ex.Message);
            _log.Error(string.Format(_local["DeleteTagFailed"], item.Name), "TagManager", ex);
        }
    }

    [RelayCommand]
    private async Task RenameTagAsync(TagEditItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name)) return;
        if (item.Name.Length > 128) { StatusMessage = _local["TagNameTooLongShort"]; return; }

        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var tag = await db.Tags.FindAsync(item.Id);
            if (tag == null) return;

            var conflict = await db.Tags.AnyAsync(t => t.Name == item.Name && t.Id != item.Id);
            if (conflict) { StatusMessage = string.Format(_local["TagNameExists"], item.Name); return; }

            tag.Name = item.Name;
            await db.SaveChangesAsync();
            _log.Info(string.Format(_local["RenameTagShort"], tag.Name), "TagManager");
            StatusMessage = string.Format(_local["TagRenamed"], item.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["ErrorBadge"], ex.Message);
            _log.Error(string.Format(_local["RenameTagFailed"], item.Name), "TagManager", ex);
        }
    }
}

/// <summary>标签编辑项</summary>
public partial class TagEditItem : ObservableObject
{
    public int Id { get; set; }
    [ObservableProperty] private string _name = "";
    public int UsageCount { get; set; }
    /// <summary>标签颜色</summary>
    public IBrush Color { get; set; } = Brushes.Transparent;
    /// <summary>新建动画标记</summary>
    public bool IsNew { get; set; }
}