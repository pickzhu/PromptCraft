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

/// <summary>批量打标签对话框 ViewModel</summary>
public partial class BatchTagModel : ViewModelBase
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly int[] _imageInfoIds;
    private readonly IBaseLogService _log;

    [ObservableProperty] private ObservableCollection<TagItem> _tags = new();
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isApplying;

    public int TargetCount => _imageInfoIds.Length;

    public string TargetCountText => string.Format(_local["BatchTagTargetCount"], TargetCount);

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(TargetCountText));
        base.OnSystemLangueChanged(data);
    }

    public BatchTagModel(
        int[] imageInfoIds,
        IDbContextFactory<ComfyDbContext> dbFactory,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log)
        : base(localizer, notice)
    {
        _imageInfoIds = imageInfoIds;
        _dbFactory = dbFactory;
        _log = log;
        _ = LoadTagsAsync();
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            // 获取所有标签
            var allTags = await db.Tags.OrderBy(t => t.Name).ToListAsync();

            // 获取选中图片已有的标签
            var assignedTagIds = await db.ImageTags
                .Where(it => _imageInfoIds.Contains(it.ImageInfoId))
                .Select(it => it.TagId)
                .Distinct()
                .ToListAsync();

            var items = allTags.Select(t => new TagItem(t)
            {
                IsSelected = assignedTagIds.Count > 0 &&
                             assignedTagIds.All(aid => assignedTagIds.Contains(t.Id))
            }).ToList();

            foreach (var item in items)
                Tags.Add(item);

            if (items.Count == 0)
                StatusMessage = _local["BatchTagNoTags"];
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["LoadFailed"], ex.Message);
            _log.Error(_local["LoadBatchTagListFailed"], "BatchTag", ex);
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsApplying = true;
        StatusMessage = _local["BatchTagApplying"];
        _log.Info(string.Format(_local["BatchTagStart"], _imageInfoIds.Length), "BatchTag");
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            var selectedTagIds = Tags.Where(t => t.IsSelected).Select(t => t.Id).ToList();

            foreach (var imageId in _imageInfoIds)
            {
                // 删除未选中的标签
                var existing = await db.ImageTags
                    .Where(it => it.ImageInfoId == imageId)
                    .ToListAsync();
                db.ImageTags.RemoveRange(existing.Where(it => !selectedTagIds.Contains(it.TagId)));

                // 添加新选中的（不重复）
                foreach (var tagId in selectedTagIds)
                {
                    if (!existing.Any(it => it.TagId == tagId))
                    {
                        db.ImageTags.Add(new ImageTag
                        {
                            ImageInfoId = imageId,
                            TagId = tagId
                        });
                    }
                }
            }

            await db.SaveChangesAsync();
            StatusMessage = string.Format(_local["BatchTagApplied"], _imageInfoIds.Length);
            _log.Info(string.Format(_local["BatchTagDone"], _imageInfoIds.Length, selectedTagIds.Count), "BatchTag");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["ErrorBadge"], ex.Message);
            _log.Error(_local["BatchTagFailed"], "BatchTag", ex);
        }
        finally
        {
            IsApplying = false;
        }
    }
}