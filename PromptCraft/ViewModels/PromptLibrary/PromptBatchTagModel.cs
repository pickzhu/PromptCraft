using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using PromptCraft.ViewModels.ComfyUI;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.PromptLibrary;

/// <summary>
/// 提示词库「打标签」对话框 ViewModel（对齐图库 BatchTagModel：全局标签池复选框列表）。
/// 预勾选 = 选中提示词已有的标签（交集的近似：出现在任一选中提示词上的标签）；应用 = 把勾选集写为这批提示词的最终标签集（替换式，经 <see cref="IPromptLibraryService.AssignTagsAsync"/> 落库并发布变更事件）。
/// </summary>
public partial class PromptBatchTagModel : ViewModelBase
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IPromptLibraryService _library;
    private readonly string[] _promptIds;
    private readonly IBaseLogService _log;

    [ObservableProperty] private ObservableCollection<TagItem> _tags = new();
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isApplying;

    public int TargetCount => _promptIds.Length;

    public string TargetCountText => string.Format(_local["PromptBatchTagTargetCount"], TargetCount);

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(TargetCountText));
        base.OnSystemLangueChanged(data);
    }

    public PromptBatchTagModel(
        string[] promptIds,
        IDbContextFactory<ComfyDbContext> dbFactory,
        IPromptLibraryService library,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log)
        : base(localizer, notice)
    {
        _promptIds = promptIds;
        _dbFactory = dbFactory;
        _library = library;
        _log = log;
        _ = LoadTagsAsync();
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            // 全局标签池（与图库/工作流同一套）
            var allTags = await db.Tags.OrderBy(t => t.Name).ToListAsync();

            // 选中提示词已有的标签（去重）
            var assignedTagIds = await db.PromptTagMaps
                .Where(m => _promptIds.Contains(m.PromptId))
                .Select(m => m.TagId)
                .Distinct()
                .ToListAsync();

            var items = allTags.Select(t => new TagItem(t)
            {
                // PromptTagMap.TagId 存全局 Tag.Id 的字符串形式，与 TagItem.Id（int）对比时转换
                IsSelected = assignedTagIds.Count > 0 &&
                             assignedTagIds.Contains(t.Id.ToString())
            }).ToList();

            foreach (var item in items)
                Tags.Add(item);

            if (items.Count == 0)
                StatusMessage = _local["PromptBatchTagNoTags"];
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["LoadFailed"], ex.Message);
            _log.Error(_local["PromptBatchTagFailedLog"], "PromptLibrary", ex);
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsApplying) return;
        IsApplying = true;
        StatusMessage = _local["PromptBatchTagApplying"];
        _log.Info(string.Format(_local["PromptBatchTagStart"], _promptIds.Length), "PromptLibrary");
        try
        {
            var selectedTagIds = Tags.Where(t => t.IsSelected).Select(t => t.Id.ToString()).ToList();
            await _library.AssignTagsAsync(_promptIds, selectedTagIds);
            StatusMessage = string.Format(_local["PromptBatchTagApplied"], _promptIds.Length);
            _log.Info(string.Format(_local["PromptBatchTagDone"], _promptIds.Length, selectedTagIds.Count), "PromptLibrary");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["ErrorBadge"], ex.Message);
            _log.Error(_local["PromptBatchTagFailed"], "PromptLibrary", ex);
        }
        finally
        {
            IsApplying = false;
        }
    }
}
