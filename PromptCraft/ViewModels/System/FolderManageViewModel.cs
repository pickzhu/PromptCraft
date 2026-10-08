using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using PromptCraft.Utils;
using PromptCraft.ViewModels.ComfyUI;
using PromptCraft.ViewModels.PromptLibrary;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.Folders;

/// <summary>文件夹管理页的列表项（首页大图标卡片：2x2 四宫格预览 + 名称 + 资产数；文件夹不分资产类型）。</summary>
public partial class FolderRow : ObservableObject
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int AssetCount { get; set; }
    /// <summary>四宫格预览（最多 4 个资产缩略图，跨类型）。</summary>
    public ObservableCollection<FolderAssetItem> Previews { get; } = new();

    /// <summary>是否有预览资产（空文件夹显示占位文案）。</summary>
    public bool HasPreview => Previews.Count > 0;

    /// <summary>四宫格固定槽位（行优先：左上→右上→左下→右下；空槽为 null 显示占位灰块）。</summary>
    public FolderAssetItem? P0 => Previews.Count > 0 ? Previews[0] : null;
    public FolderAssetItem? P1 => Previews.Count > 1 ? Previews[1] : null;
    public FolderAssetItem? P2 => Previews.Count > 2 ? Previews[2] : null;
    public FolderAssetItem? P3 => Previews.Count > 3 ? Previews[3] : null;

    /// <summary>预览集合填充完成后调用（通知 HasPreview 与四个固定槽位刷新）。</summary>
    public void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(P0));
        OnPropertyChanged(nameof(P1));
        OnPropertyChanged(nameof(P2));
        OnPropertyChanged(nameof(P3));
    }
}

/// <summary>文件夹内的资产项（内容列表 / 预览四宫格共用）。</summary>
public partial class FolderAssetItem : ObservableObject
{
    public string Scope { get; set; } = "";
    /// <summary>资产主键字符串（Prompt=Guid；Workflow/Gallery=int）。</summary>
    public string AssetId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeDisplay { get; set; } = "";
    /// <summary>副文本（更新时间等）。</summary>
    public string SubText { get; set; } = "";
    public string ThumbPath { get; set; } = "";
    public Bitmap? Thumb { get; set; }

    /// <summary>排序时间（跨类型合并时按此排序取前 N）。</summary>
    public DateTime SortTime { get; set; }

    /// <summary>类型标注颜色（与文件夹卡片一致）。</summary>
    public IBrush ScopeBrush => Scope switch
    {
        FolderScopes.Workflow => new SolidColorBrush(Color.Parse("#4A90D9")),
        FolderScopes.Prompt => new SolidColorBrush(Color.Parse("#4CAF50")),
        FolderScopes.Gallery => new SolidColorBrush(Color.Parse("#AB7BD9")),
        _ => Brushes.Gray,
    };

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 文件夹管理页（统一单级文件夹，不分资产类型）。
/// 默认首页 = 全部文件夹大图标列表（2x2 四宫格预览）；双击文件夹进入内部显示跨类型资产（类型徽标区分），顶部出现返回按钮。
/// 所有操作进右键菜单（列表空白右键=新建[仅名称]；文件夹卡片右键=打开/粘贴/重命名/删除[带资产开关二次确认]；资产右键=编辑或详情/从文件夹移除/加入其他文件夹/复制/删除）。
/// </summary>
public partial class FolderManageViewModel : ViewModelBase
{
    private readonly IServiceProvider _service;
    private readonly IFolderService _folderService;
    private readonly IBaseLogService _log;
    private IWorkspaceService? _workspace;

    [ObservableProperty] private ObservableCollection<FolderRow> _folders = new();
    [ObservableProperty] private ObservableCollection<FolderAssetItem> _folderAssets = new();
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _contentsTitle = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>两级浏览：是否处于文件夹内部（内部态显示资产列表 + 顶部返回按钮）。</summary>
    [ObservableProperty] private bool _isInsideFolder;

    /// <summary>当前打开的文件夹（内部态）。</summary>
    [ObservableProperty] private FolderRow? _currentFolder;

    /// <summary>跨页共享剪贴板（复制=加入其他文件夹 的跨文件夹中转；与提示词库复制共用）。</summary>
    private readonly IFolderClipboardService _clipboard;

    /// <summary>剪贴板是否有可粘贴资产（内容区标题栏"粘贴"按钮可用性）。</summary>
    public bool HasClipboard => _clipboard.HasItems;
    private void NotifyClipboardChanged() => OnPropertyChanged(nameof(HasClipboard));

    public FolderManageViewModel(
        IServiceProvider service,
        IFolderService folderService,
        ILocalizer localizer,
        IBaseNotice notice,
        IBaseLogService log)
        : base(localizer, notice)
    {
        _service = service;
        _folderService = folderService;
        _log = log;
        _clipboard = service.GetRequiredService<IFolderClipboardService>();
        _clipboard.Changed += NotifyClipboardChanged;
        _displayName = "FOLDERMANAGE";
        _icon = MaterialIconKind.FolderOutline;
        _index = 30;
        _sideMenu = true;

        _noticeService.Subscribe(EventNameConst.PromptLibraryChangedEvent, OnFoldersChanged);
    }

    public override void Disposed()
    {
        _clipboard.Changed -= NotifyClipboardChanged;
        _noticeService.Unsubscribe(EventNameConst.PromptLibraryChangedEvent, OnFoldersChanged);
        base.Disposed();
    }

    private async void OnFoldersChanged(object? _) => await LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading) return; // 防重入：事件刷新与页面 OnLoaded 可能并发触发
        IsLoading = true;
        try
        {
            var rows = new List<FolderRow>();
            var folders = await _folderService.GetAllFoldersAsync();
            var counts = await GetAssetCountsAsync();
            foreach (var f in folders)
            {
                var row = new FolderRow
                {
                    Id = f.Id,
                    Name = f.Name,
                    AssetCount = counts.TryGetValue(f.Id, out var n) ? n : 0,
                };
                // 2x2 四宫格预览：跨类型取前 4 个资产缩略图（空文件夹 Previews 为空，XAML 显示占位）
                foreach (var preview in await LoadAssetsCoreAsync(f.Id, 4))
                    row.Previews.Add(preview);
                row.NotifyPreviewChanged();
                rows.Add(row);
            }
            Folders = new ObservableCollection<FolderRow>(rows);

            // 已打开的文件夹内容同步刷新（内容可能被外部改动）
            if (CurrentFolder != null)
            {
                var opened = Folders.FirstOrDefault(r => r.Id == CurrentFolder.Id);
                if (opened != null)
                {
                    CurrentFolder = opened;
                    await LoadFolderContentsAsync(opened);
                }
                else
                {
                    CurrentFolder = null;
                    IsInsideFolder = false;
                    FolderAssets.Clear();
                    ContentsTitle = "";
                }
            }
            StatusMessage = Folders.Count == 0 ? _local["FolderListEmpty"] : "";
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["FolderSaveFailed"], ex.Message);
            _log.Error(string.Format(_local["FolderSaveFailed"] ?? "", ex.Message), "FolderManage", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string TypeDisplayFor(string scope) => scope switch
    {
        FolderScopes.Workflow => _local["FolderScopeWorkflow"] ?? "工作流",
        FolderScopes.Prompt => _local["FolderScopePrompt"] ?? "提示词",
        FolderScopes.Gallery => _local["FolderScopeGallery"] ?? "图库",
        _ => scope,
    };

    private static IBrush ScopeBrushFor(string scope) => scope switch
    {
        FolderScopes.Workflow => new SolidColorBrush(Color.Parse("#4A90D9")),
        FolderScopes.Prompt => new SolidColorBrush(Color.Parse("#4CAF50")),
        FolderScopes.Gallery => new SolidColorBrush(Color.Parse("#AB7BD9")),
        _ => Brushes.Gray,
    };

    /// <summary>各文件夹内的资产总数（跨类型：按三张关联表行数累计，一个资产在多个文件夹内各计一次）。</summary>
    private async Task<Dictionary<int, int>> GetAssetCountsAsync()
    {
        var result = new Dictionary<int, int>();
        await using var db = await _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>()
            .CreateDbContextAsync();
        foreach (var g in await db.PromptFolderMaps.AsNoTracking()
                     .GroupBy(m => m.FolderId).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
            result[g.Key] = result.GetValueOrDefault(g.Key) + g.N;
        foreach (var g in await db.GalleryFolderMaps.AsNoTracking()
                     .GroupBy(m => m.FolderId).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
            result[g.Key] = result.GetValueOrDefault(g.Key) + g.N;
        foreach (var g in await (from m in db.WorkflowFolderMaps.AsNoTracking()
                                 join w in db.Workflows.AsNoTracking() on m.WorkflowId equals w.Id
                                 where !w.IsDeleted
                                 group m by m.FolderId into grp
                                 select new { Key = grp.Key, N = grp.Count() }).ToListAsync())
            result[g.Key] = result.GetValueOrDefault(g.Key) + g.N;
        return result;
    }

    /// <summary>双击文件夹 → 进入内部（显示该文件夹全部跨类型资产 + 顶部返回按钮）。</summary>
    [RelayCommand]
    private async Task OpenFolderAsync(FolderRow? row)
    {
        if (row == null) return;
        CurrentFolder = row;
        IsInsideFolder = true;
        await LoadFolderContentsAsync(row);
    }

    /// <summary>返回文件夹列表（退出内部态）。</summary>
    [RelayCommand]
    private void BackToFolders()
    {
        IsInsideFolder = false;
        CurrentFolder = null;
        FolderAssets.Clear();
        ContentsTitle = "";
    }

    private async Task LoadFolderContentsAsync(FolderRow row)
    {
        FolderAssets = new ObservableCollection<FolderAssetItem>(await LoadAssetsCoreAsync(row.Id, 200));
        ContentsTitle = $"{row.Name}（{FolderAssets.Count}）";
    }

    /// <summary>取某文件夹内资产（跨类型合并：提示词/工作流/图库，按更新时间排序取前 limit；预览与内容列表共用）。</summary>
    private async Task<List<FolderAssetItem>> LoadAssetsCoreAsync(int folderId, int limit)
    {
        var items = new List<FolderAssetItem>();
        await using var db = await _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>().CreateDbContextAsync();

        // 提示词
        {
            var q = await (from m in db.PromptFolderMaps.AsNoTracking()
                           join p in db.Prompts.AsNoTracking() on m.PromptId equals p.Id
                           where m.FolderId == folderId
                           orderby p.UpdatedAt descending
                           select new { p.Id, p.Title, p.Cover, p.UpdatedAt }).Take(limit).ToListAsync();
            var coversDir = (_workspace ??= _service.GetRequiredService<IWorkspaceService>()).CoversDir;
            foreach (var r in q)
                items.Add(new FolderAssetItem
                {
                    Scope = FolderScopes.Prompt,
                    AssetId = r.Id,
                    Name = r.Title,
                    TypeDisplay = TypeDisplayFor(FolderScopes.Prompt),
                    SubText = r.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    SortTime = r.UpdatedAt,
                    ThumbPath = string.IsNullOrWhiteSpace(r.Cover) ? "" : Path.Combine(coversDir, r.Cover),
                });
        }
        // 图库
        {
            var settings = _service.GetRequiredService<ComfySettings>();
            var q = await (from m in db.GalleryFolderMaps.AsNoTracking()
                           join i in db.ImageMetadata.AsNoTracking() on m.ImageInfoId equals i.Id
                           where m.FolderId == folderId
                           orderby i.ImportedAt descending
                           select i).Take(limit).ToListAsync();
            foreach (var i in q)
                items.Add(new FolderAssetItem
                {
                    Scope = FolderScopes.Gallery,
                    AssetId = i.Id.ToString(),
                    Name = i.FileName,
                    TypeDisplay = TypeDisplayFor(FolderScopes.Gallery),
                    SubText = $"{i.Width}×{i.Height} · {FormatSize(i.FileSize)}",
                    SortTime = i.ImportedAt,
                    ThumbPath = GetGalleryThumbPath(settings, i),
                });
        }
        // 工作流（缩略图回退链对齐工作流管理页 LoadThumbnailAsync：手动设置 → 任务输出首图 → GUID 匹配 → 占位图）
        {
            var settings = _service.GetRequiredService<ComfySettings>();
            var q = await (from m in db.WorkflowFolderMaps.AsNoTracking()
                           join w in db.Workflows.AsNoTracking() on m.WorkflowId equals w.Id
                           where m.FolderId == folderId && !w.IsDeleted
                           orderby w.UpdatedAt descending
                           select new { w.Id, w.Name, w.ThumbnailPath, w.UpdatedAt, w.WorkflowJsonBlob }).Take(limit).ToListAsync();
            foreach (var r in q)
            {
                var thumbPath = r.ThumbnailPath ?? "";
                if (string.IsNullOrWhiteSpace(thumbPath) || !File.Exists(thumbPath))
                {
                    // 回退 2：任务输出第一张图（对齐 ComfyWorkflowModel）
                    try
                    {
                        var rel = await db.JobOutputs.AsNoTracking()
                            .Where(x => x.Job.WorkflowId == r.Id && !x.IsDeleted && x.ImageInfoId != null)
                            .OrderBy(x => x.Job.CreatedAt)
                            .Select(x => x.Image!.RelativePath)
                            .FirstOrDefaultAsync();
                        if (rel != null)
                        {
                            var path = Path.Combine(settings.ComfyOutputDir, rel);
                            if (File.Exists(path)) thumbPath = path;
                        }
                    }
                    catch { }

                    // 回退 3：GUID 匹配（工作流 JSON 顶级 id == 图片提取工作流 WorkflowGuid → 取该工作流第一张图；
                    //         详情页/管理页多数真实缩略图来自这一级）
                    if (string.IsNullOrWhiteSpace(thumbPath))
                    {
                        try
                        {
                            var json = ComfyMetadataCodec.Decompress(r.WorkflowJsonBlob);
                            var wfGuid = ExtractWorkflowGuid(json ?? "");
                            if (!string.IsNullOrEmpty(wfGuid))
                            {
                                var rel = await db.Workflows.AsNoTracking()
                                    .Where(x => x.WorkflowGuid == wfGuid && (x.Source == "image" || x.Source == "video") && !x.IsDeleted)
                                    .SelectMany(x => x.Images)
                                    .OrderBy(x => x.CreatedAt)
                                    .Select(x => x.RelativePath)
                                    .FirstOrDefaultAsync();
                                if (rel != null)
                                {
                                    var path = Path.Combine(settings.ComfyOutputDir, rel);
                                    if (File.Exists(path))
                                    {
                                        // 视频产物：预览用首帧封面缩略图（jpg），避免直接解码视频
                                        thumbPath = PromptCraft.Service.ComfyMediaKinds.IsVideo(path)
                                            ? Path.Combine(settings.ThumbDir,
                                                PromptCraft.Service.ImageSyncService.GetThumbFileName(rel, settings.ThumbMaxDimension))
                                            : path;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                items.Add(new FolderAssetItem
                {
                    Scope = FolderScopes.Workflow,
                    AssetId = r.Id.ToString(),
                    Name = r.Name,
                    TypeDisplay = TypeDisplayFor(FolderScopes.Workflow),
                    SubText = r.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    SortTime = r.UpdatedAt,
                    ThumbPath = thumbPath,
                });
            }
        }

        // 跨类型按更新时间合并排序，取前 limit；内容列表只解码前 48 个缩略图防卡顿，预览（limit≤4）全部解码
        items = items.OrderByDescending(x => x.SortTime).Take(limit).ToList();
        var decodeLimit = limit <= 4 ? items.Count : Math.Min(items.Count, 48);
        for (var i = 0; i < decodeLimit; i++)
        {
            var item = items[i];
            // 工作流无封面/无任务输出时回退占位图（对齐工作流管理页 LoadThumbnailAsync 末级回退）
            if (item.Scope == FolderScopes.Workflow && string.IsNullOrWhiteSpace(item.ThumbPath))
            {
                item.Thumb = LoadWorkflowPlaceholder();
                continue;
            }
            item.Thumb = LoadBitmapSafe(item.ThumbPath);
        }
        return items;
    }

    /// <summary>工作流占位图（avares 资源，对齐 ComfyWorkflowModel.LoadPlaceholderBitmap）。</summary>
    private static Bitmap? LoadWorkflowPlaceholder()
    {
        try
        {
            var uri = new Uri("avares://PromptCraft/Assets/workflow_placeholder.png");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从工作流 JSON 提取顶级 "id"（ComfyUI 工作流 UUID）；无则返回 null（对齐 ComfyWorkflowModel）。</summary>
    private static string? ExtractWorkflowGuid(string workflowJson)
    {
        try
        {
            if (string.IsNullOrEmpty(workflowJson)) return null;
            using var doc = global::System.Text.Json.JsonDocument.Parse(workflowJson);
            if (doc.RootElement.ValueKind != global::System.Text.Json.JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != global::System.Text.Json.JsonValueKind.String) return null;
            var s = id.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private string GetGalleryThumbPath(ComfySettings settings, ImageInfo img)
    {
        var thumbDir = settings.ThumbDir;
        var newPath = Path.Combine(thumbDir, ImageSyncService.GetThumbFileName(img.RelativePath, settings.ThumbMaxDimension));
        if (File.Exists(newPath)) return newPath;
        var legacy = Path.Combine(thumbDir, $"{Path.GetFileNameWithoutExtension(img.FileName)}_{settings.ThumbMaxDimension}.jpg");
        return File.Exists(legacy) ? legacy : newPath;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{(double)bytes / 1024 / 1024:0.0} MB",
        >= 1024 => $"{(double)bytes / 1024:0} KB",
        _ => $"{bytes} B",
    };

    private static Bitmap? LoadBitmapSafe(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (new FileInfo(path).Length > 3 * 1024 * 1024) return null;
            return new Bitmap(path);
        }
        catch
        {
            return null;
        }
    }

    // ---- 右键菜单：新建（空白区）/ 重命名 / 删除（带资产开关二次确认） ----

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        // 新建文件夹不选资产类型：文件夹可放任意类型资产（Scope 统一为 FolderScopes.All）
        var nameBox = new TextBox
        {
            Text = "",
            PlaceholderText = _local["FolderNamePlaceholder"],
            MinWidth = 220,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = _local["FolderNamePlaceholder"], FontSize = 12, Foreground = Brushes.Gray, Margin = new Avalonia.Thickness(0, 0, 0, 4) },
                nameBox,
            },
            Margin = new Avalonia.Thickness(4),
        };

        var ok = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = panel,
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["FolderNew"], MinWidth = 320 });
        if (ok is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;
        var name = nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _folderService.SaveFolderAsync(new Folder { Name = name, Scope = FolderScopes.All });
            StatusMessage = "";
            _log.Info(string.Format(_local["FolderCreatedLog"] ?? "", name), "FolderManage");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Warn(ex.Message, "FolderManage", ex);
        }
    }

    [RelayCommand]
    private async Task RenameFolderAsync(FolderRow? row)
    {
        if (row == null) return;
        var textBox = new TextBox
        {
            Text = row.Name,
            PlaceholderText = _local["FolderNamePlaceholder"],
            MinWidth = 280,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var result = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = textBox,
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["FolderRename"], MinWidth = 340 });
        if (result is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;
        var name = textBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == row.Name) return;

        try
        {
            await _folderService.SaveFolderAsync(new Folder { Id = row.Id, Name = name, Scope = FolderScopes.All });
            _log.Info(string.Format(_local["FolderRenamedLog"] ?? "", row.Name, name), "FolderManage");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Warn(ex.Message, "FolderManage", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(FolderRow? row)
    {
        if (row == null) return;

        // 第一次确认
        var ok1 = await ConfirmAsync(string.Format(_local["FolderDeleteConfirm"], row.Name));
        if (!ok1) return;

        // 第二次确认：带"同时删除资产"开关（默认关闭）
        var checkBox = new CheckBox
        {
            Content = _local["FolderDeleteAssetsSwitch"],
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hint = new TextBlock
        {
            Text = _local["FolderDeleteAssetsHint"],
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };
        var panel = new StackPanel { Children = { checkBox, hint }, Margin = new Avalonia.Thickness(4) };

        var ok2 = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = panel,
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["FolderDelete"], MinWidth = 360 });
        if (ok2 is not SukiMessageBoxResult r2 || !r2.Equals(SukiMessageBoxResult.OK)) return;

        var deleteAssets = checkBox.IsChecked == true;

        try
        {
            await _folderService.DeleteFolderAsync(row.Id, deleteAssets);
            StatusMessage = string.Format(_local["FolderDeleteSuccess"], row.Name);
            _log.Info(string.Format(_local["FolderDeletedLog"] ?? "", row.Name, deleteAssets), "FolderManage");
            if (CurrentFolder?.Id == row.Id) { CurrentFolder = null; IsInsideFolder = false; FolderAssets.Clear(); ContentsTitle = ""; }
            await LoadAsync();
            ShowToast(_local["FolderDeleteSuccess"], row.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Warn(ex.Message, "FolderManage", ex);
        }
    }

    // ---- 内容项右键菜单：编辑或详情 / 从文件夹移除 / 加入其他文件夹 / 复制 / 删除 ----

    /// <summary>打开资产详情或编辑（提示词=编辑弹窗；工作流/图库=详情弹窗）。</summary>
    [RelayCommand]
    private async Task OpenAssetAsync(FolderAssetItem? asset)
    {
        if (asset == null) return;
        try
        {
            switch (asset.Scope)
            {
                case FolderScopes.Prompt:
                {
                    var library = _service.GetRequiredService<IPromptLibraryService>();
                    var prompt = await library.GetPromptAsync(asset.AssetId);
                    if (prompt == null) return;
                    await PromptEditDialogOpener.OpenEditAsync(
                        _service, prompt, _local["PromptEdit"] ?? _local["FolderOpen"] ?? "编辑",
                        _local, _noticeService, _log, onSaved: () => _ = LoadAsync());
                    break;
                }
                case FolderScopes.Workflow:
                {
                    var item = new WorkflowItem
                    {
                        Id = int.Parse(asset.AssetId),
                        Name = asset.Name,
                        Source = WorkflowSource.Local,
                        SortTime = DateTime.Now,
                    };
                    await OpenWorkflowDetailAsync(item);
                    break;
                }
                case FolderScopes.Gallery:
                {
                    await OpenGalleryDetailAsync(asset);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["FolderSaveFailed"] ?? "", ex.Message), "FolderManage", ex);
        }
    }

    private async Task OpenWorkflowDetailAsync(WorkflowItem item)
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }
        var settings = _service.GetRequiredService<ComfySettings>();
        var dbFactory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
        var detailVm = new WorkflowDetailModel(item, settings, dbFactory, _service, _local, _noticeService, _log);
        var viewService = _service.GetRequiredService<IBaseViewService>();
        if (!viewService.TryCreateView(detailVm, out var detailView, "WorkflowDetail")) return;

        var host = new SukiMessageBoxHost
        {
            Content = detailView,
            IconPreset = null,
            Width = 900,
            ActionButtonsPreset = SukiMessageBoxButtons.Close,
        };
        var window = SukiMessageBox.CreateMessageBoxWindow(new SukiMessageBoxOptions
        {
            Title = _local["WorkflowDetail"] ?? item.Name,
            WindowStartupLocation = WindowStartupLocation.Manual,
        });
        detailVm.RequestClose += () => window.Close();
        window.Content = host;
        if (host.ActionButtonsSource is { } buttons)
            for (var i = 0; i < buttons.Count; i++)
                buttons[i].Click += (_, _) => window.Close();
        window.KeyUp += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) window.Close(); };
        await window.ShowDialog(owner);
    }

    private async Task OpenGalleryDetailAsync(FolderAssetItem asset)
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }
        await using var db = await _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>().CreateDbContextAsync();
        var img = await db.ImageMetadata.AsNoTracking().FirstOrDefaultAsync(i => i.Id == int.Parse(asset.AssetId));
        if (img == null) return;
        var settings = _service.GetRequiredService<ComfySettings>();
        var item = new ImageItem
        {
            ImageInfoId = img.Id,
            FileName = img.FileName,
            Width = img.Width,
            Height = img.Height,
            FileSize = img.FileSize,
            Hash = img.Hash,
            RelativePath = img.RelativePath,
            OutputDir = settings.ComfyOutputDir,
            CreatedAt = img.CreatedAt,
            Thumbnail = asset.Thumb,
        };
        var detailVm = new ImageDetailModel(item, settings,
            _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>(), _service, _local, _noticeService, _log);
        var viewService = _service.GetRequiredService<IBaseViewService>();
        if (!viewService.TryCreateView(detailVm, out var detailView, "ImageDetail")) return;

        var host = new SukiMessageBoxHost
        {
            Content = detailView,
            IconPreset = null,
            Width = 900,
            ActionButtonsPreset = SukiMessageBoxButtons.Close,
        };
        var window = SukiMessageBox.CreateMessageBoxWindow(new SukiMessageBoxOptions
        {
            Title = _local["ImageDetailTitle"] ?? asset.Name,
            WindowStartupLocation = WindowStartupLocation.Manual,
        });
        detailVm.Deleted += () => window.Close();
        window.Content = host;
        if (host.ActionButtonsSource is { } buttons)
            for (var i = 0; i < buttons.Count; i++)
                buttons[i].Click += (_, _) => window.Close();
        window.KeyUp += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) window.Close(); };
        await window.ShowDialog(owner);
    }

    /// <summary>从当前文件夹移除资产（资产本身保留）。</summary>
    [RelayCommand]
    private async Task RemoveFromFolderAsync(FolderAssetItem? asset)
    {
        if (asset == null || CurrentFolder == null) return;
        try
        {
            await _folderService.RemoveFromFoldersAsync(asset.Scope, new[] { asset.AssetId }, new[] { CurrentFolder.Id });
            _log.Info(string.Format(_local["FolderAssetRemoveLog"] ?? "", asset.Name), "FolderManage");
            ShowToast(string.Format(_local["FolderRemoveDone"] ?? "", asset.Name), "");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Warn(ex.Message, "FolderManage", ex);
        }
    }

    /// <summary>把资产加入其他文件夹（复制语义=跨文件夹关联）。</summary>
    [RelayCommand]
    private async Task AddAssetToFolderAsync(FolderAssetItem? asset)
    {
        if (asset == null) return;
        var picked = await FolderPickerDialog.PickAsync(asset.Scope, null, _service);
        if (picked == null) return;
        if (picked.Count == 0)
        {
            ShowToast(_local["AddToFolderNone"] ?? "", "");
            return;
        }
        try
        {
            await _folderService.AddToFoldersAsync(asset.Scope, new[] { asset.AssetId }, picked);
            _log.Info(string.Format(_local["FolderAssetAddLog"] ?? "", 1, picked.Count), "FolderManage");
            ShowToast(string.Format(_local["AddToFolderDone"] ?? "", 1, picked.Count), "");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Warn(ex.Message, "FolderManage", ex);
        }
    }

    /// <summary>复制到剪贴板（粘贴到目标文件夹 = 加入关联；跨页共享剪贴板，提示词库复制同样入此）。</summary>
    [RelayCommand]
    private void CopyAsset(FolderAssetItem? asset)
    {
        if (asset == null) return;
        _clipboard.Copy(new[] { asset });
        _log.Info(string.Format(_local["FolderCopiedLog"] ?? "", asset.Name), "FolderManage");
        ShowToast(string.Format(_local["FolderCopiedHint"] ?? "", 1), "");
    }

    /// <summary>把剪贴板中的资产加入目标文件夹（跨文件夹快速关联；参数为空则用当前打开的文件夹）。
    /// 文件夹不分资产类型：剪贴板可含跨类型资产，按各自 Scope 分组写入关联。</summary>
    [RelayCommand]
    private async Task PasteToFolderAsync(FolderRow? target = null)
    {
        var folder = target ?? CurrentFolder;
        if (folder == null)
        {
            ShowToast(_local["FolderPasteNoTarget"] ?? "", "");
            return;
        }
        if (!_clipboard.HasItems)
        {
            ShowToast(_local["FolderPasteEmpty"] ?? "", "");
            return;
        }
        try
        {
            foreach (var grp in _clipboard.Items.GroupBy(a => a.Scope))
                await _folderService.AddToFoldersAsync(grp.Key,
                    grp.Select(a => a.AssetId).ToList(), new[] { folder.Id });
            _log.Info(string.Format(_local["FolderPastedLog"] ?? "", _clipboard.Items.Count, folder.Name), "FolderManage");
            ShowToast(string.Format(_local["FolderCopiedHint"] ?? "", _clipboard.Items.Count), "");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Warn(ex.Message, "FolderManage", ex);
        }
    }

    /// <summary>删除资产（提示词物理删；工作流逻辑删；图库物理删文件+缩略图+记录）。</summary>
    [RelayCommand]
    private async Task DeleteAssetAsync(FolderAssetItem? asset)
    {
        if (asset == null) return;
        var ok = await ConfirmAsync(string.Format(_local["FolderAssetDeleteConfirm"], asset.Name));
        if (!ok) return;
        try
        {
            switch (asset.Scope)
            {
                case FolderScopes.Prompt:
                {
                    var library = _service.GetRequiredService<IPromptLibraryService>();
                    await library.DeletePromptsAsync(new[] { asset.AssetId });
                    break;
                }
                case FolderScopes.Workflow:
                {
                    await using var db = await _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>().CreateDbContextAsync();
                    var id = int.Parse(asset.AssetId);
                    await db.Workflows.Where(w => w.Id == id)
                        .ExecuteUpdateAsync(s => s.SetProperty(w => w.IsDeleted, true));
                    await db.WorkflowFolderMaps.Where(m => m.WorkflowId == id).ExecuteDeleteAsync();
                    break;
                }
                case FolderScopes.Gallery:
                {
                    var sync = _service.GetRequiredService<IImageSyncService>();
                    var result = await sync.DeleteImagesAsync(new[] { int.Parse(asset.AssetId) });
                    if (result.Failed > 0)
                    {
                        StatusMessage = string.Format(_local["FolderSaveFailed"] ?? "", string.Join("; ", result.Errors));
                        return;
                    }
                    break;
                }
            }
            _log.Info(string.Format(_local["FolderAssetDeleteLog"] ?? "", asset.Name), "FolderManage");
            ShowToast(string.Format(_local["FolderAssetDeleteDone"] ?? "", asset.Name), "");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log.Warn(ex.Message, "FolderManage", ex);
        }
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = message,
                    Margin = new Avalonia.Thickness(4),
                    TextWrapping = TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["FolderDelete"], MinWidth = 360 });
        return confirm is SukiMessageBoxResult r && r.Equals(SukiMessageBoxResult.OK);
    }

    private void ShowToast(string title, string content)
    {
        try
        {
            var toastManager = _service.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
            if (toastManager == null) return;
            var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3);
            toast.Queue();
        }
        catch (Exception ex)
        {
            _log.Debug(string.Format(_local["PromptToastFailed"] ?? "", ex.Message), "FolderManage");
        }
    }

    public override void OnSystemLangueChanged(object? data)
    {
        _ = LoadAsync();
    }
}
