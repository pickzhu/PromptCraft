using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Common;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using PromptCraft.ViewModels.ComfyUI;
using Ke.Bee.Localization.Localizer.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.PromptLibrary;

/// <summary>模型分组下拉的单个选项。</summary>
public partial class PromptModelOption : ObservableObject
{
    public string Name { get; set; } = "";
}

/// <summary>模型分组下拉的分组（图片 / 视频）。</summary>
public partial class PromptModelGroup : ObservableObject
{
    public string Label { get; set; } = "";
    public ObservableCollection<PromptModelOption> Options { get; } = new();
}

/// <summary>
/// 提示词编辑对话框 ViewModel（对齐 PromptMaster PromptEditForm）：
/// 标题 / 封面（选择图片复制到工作空间 covers）/ 标签多选（可即时新建）/
/// 备注 / 种子 / 模型 / 正向与负向提示词（带复制按钮）。保存走 <see cref="PromptLibraryService.SavePromptAsync"/>；
/// 文件夹归属编辑时保持不变（弹窗无文件夹选择 UI）。
/// </summary>
public partial class PromptEditModel : ViewModelBase
{
    private readonly IPromptLibraryService _library;
    private readonly IFolderService _folderService;
    private readonly IWorkspaceService _workspace;
    private readonly IServiceProvider _service;
    private readonly IBaseLogService _log;
    private readonly string _promptId;
    private readonly DateTime _createdAt;
    private readonly List<PromptTagMap> _existingTagMap;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _positive = "";
    [ObservableProperty] private string _negative = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _seed = "";
    [ObservableProperty] private string _models = "";
    [ObservableProperty] private string _cover = "";
    [ObservableProperty] private Bitmap? _coverBitmap;
    [ObservableProperty] private bool _isCoverLoading;
    [ObservableProperty] private ObservableCollection<PromptTagItem> _tags = new();
    [ObservableProperty] private List<string> _selectedTagIds = new();
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSaving;

    /// <summary>已选文件夹 Id 集合（多对多；空=未分类）。编辑时从服务加载原归属，保存时保持不变（编辑弹窗已无文件夹选择 UI）。</summary>
    [ObservableProperty] private List<int> _selectedFolderIds = new();

    /// <summary>模型分组下拉（图片 / 视频两组单选，选项来自源库 PromptMaster 列表）。</summary>
    public ObservableCollection<PromptModelGroup> ModelGroups { get; } = new();

    /// <summary>保存成功（宿主订阅：刷新列表并关闭弹窗）。</summary>
    public event Action? Saved;

    public PromptEditModel(
        Prompt? existing,
        IPromptLibraryService library,
        IFolderService folderService,
        IWorkspaceService workspace,
        IServiceProvider service,
        ILocalizer localizer,
        IBaseNotice notice,
        IBaseLogService log)
        : base(localizer, notice)
    {
        _library = library;
        _folderService = folderService;
        _workspace = workspace;
        _service = service;
        _log = log;

        // 模型分组数据（源库 PromptMaster 列表：图片 / 视频）
        var imageModels = new[]
        {
            "AbsoluteReality", "AbyssOrangeMix", "AlbedoBase XL", "Animagine XL", "Anima", "Anima aesthetic",
            "Anything V5", "AuraFlow", "ChilloutMix", "Chroma", "Counterfeit", "CyberRealistic",
            "CyberRealistic XL", "DALL-E", "Deliberate", "Doubao", "DreamShaper", "DreamShaper XL",
            "EpicRealism", "Firefly", "Flux.1 Dev", "Flux.1 Kontext", "Flux.1 Pro", "Flux.1 Schnell",
            "Flux2", "Flux1", "GhostMix", "Hassaku", "HiDream", "HunyuanDiT", "Ideogram", "Illustrious",
            "Illustrious XL", "Imagen", "Jimeng", "Juggernaut XL", "Kolors", "Krea", "Krea2", "Leonardo",
            "majicMIX", "MeinaMix", "Midjourney", "NoobAI", "NoobAI XL", "NovelAI", "Nova", "PixArt",
            "Playground", "Pony", "Pony Diffusion V6 XL", "PrefectIllustrious", "Proteus", "Qwen-Image",
            "RavenMix", "RealCartoon", "Realistic Vision", "RealVisXL", "Recraft", "SD 1.5", "SD 3.5",
            "SD 3.5 Large", "SD 3.5 Medium", "SDXL", "SDXL Lightning", "SDXL Turbo", "Seedream",
            "Stable Cascade", "WAI-Illustrious", "Z-Image", "ZavyChromaXL",
        };
        var videoModels = new[]
        {
            "AnimateDiff", "CogVideoX", "CogVideoX1.5", "DynamiCrafter", "FramePack", "Hailuo", "HappyHorse",
            "Helios", "HunyuanVideo", "HunyuanVideo 1.5", "I2VGen-XL", "Kling", "Kling 2.6", "Kling 3.0",
            "LTX", "LTX-2", "LTX-2.3", "Luma Dream Machine", "Luma Ray", "Luma Ray3", "Mochi", "Open-Sora",
            "Open-Sora 2.0", "Pika", "Runway Gen-3", "Runway Gen-4", "Runway Gen-4.5", "Seedance",
            "Seedance 1.5", "Seedance 2.0", "SkyReels", "Sora", "Sora 2", "SVD", "Veo", "Veo 2", "Veo 3",
            "Veo 3.1", "Vidu", "Wan2.1", "Wan2.2", "Wan2.7",
        };
        var imageGroup = new PromptModelGroup { Label = _local["PromptModelGroupImage"] ?? "图片" };
        foreach (var n in imageModels) imageGroup.Options.Add(new PromptModelOption { Name = n });
        ModelGroups.Add(imageGroup);

        var videoGroup = new PromptModelGroup { Label = _local["PromptModelGroupVideo"] ?? "视频" };
        foreach (var n in videoModels) videoGroup.Options.Add(new PromptModelOption { Name = n });
        ModelGroups.Add(videoGroup);

        if (existing != null)
        {
            _promptId = existing.Id;
            _createdAt = existing.CreatedAt;
            Title = existing.Title;
            Positive = existing.Positive;
            Negative = existing.Negative;
            Note = existing.Note;
            Seed = existing.Seed;
            Models = existing.Models;
            Cover = existing.Cover;
            _existingTagMap = existing.PromptTags?.ToList() ?? new List<PromptTagMap>();
        }
        else
        {
            // 对齐 PmLibrary Va()：新建时先分配 UUID
            _promptId = Guid.NewGuid().ToString("N");
            _createdAt = DateTime.UtcNow;
            Title = _local["PromptNewDefaultTitle"];
            _existingTagMap = new List<PromptTagMap>();
        }

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // 保留该提示词当前的文件夹归属（编辑弹窗无文件夹选择 UI，保存时不改归属）
            SelectedFolderIds = (await _folderService.GetAssetFolderIdsAsync(FolderScopes.Prompt, _promptId)).ToList();

            var tags = await _library.GetTagsAsync();
            var selected = _existingTagMap.Select(t => t.TagId).ToHashSet();
            Tags = new ObservableCollection<PromptTagItem>(tags.Select(t =>
            {
                var item = new PromptTagItem(t) { IsSelected = selected.Contains(t.Id.ToString()) };
                return item;
            }));
            SelectedTagIds = Tags.Where(t => t.IsSelected).Select(t => t.Id).ToList();

            if (!string.IsNullOrEmpty(Cover))
            {
                await LoadCoverAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptInitFailed"], ex.Message);
            _log.Error(_local["PromptInitFailedLog"], "PromptLibrary", ex);
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

    /// <summary>保存：组装 Prompt 调用服务写入（upsert），成功触发 Saved 事件。</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            StatusMessage = _local["PromptTitleRequired"];
            return;
        }

        IsSaving = true;
        try
        {
            var prompt = new Prompt
            {
                Id = _promptId,
                Title = Title.Trim(),
                Positive = Positive ?? "",
                Negative = Negative ?? "",
                Note = Note ?? "",
                Seed = Seed ?? "",
                Models = Models ?? "",
                Cover = Cover ?? "",
                FolderIds = SelectedFolderIds.ToList(),
                CreatedAt = _createdAt,
                UpdatedAt = DateTime.UtcNow,
                PromptTags = SelectedTagIds.Select(id => new PromptTagMap { PromptId = _promptId, TagId = id }).ToList(),
            };
            await _library.SavePromptAsync(prompt);
            _log.Info(string.Format(_local["PromptSavedLog"] ?? "", Title), "PromptLibrary");
            StatusMessage = _local["PromptSaved"];
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptSaveFailed"], ex.Message);
            _log.Error(string.Format(_local["PromptSaveFailedLog"], Title), "PromptLibrary", ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>模型分组下拉：单选模型 → 写入 Models。</summary>
    [RelayCommand]
    private void SelectModel(PromptModelOption? option)
    {
        if (option != null) Models = option.Name;
    }

    /// <summary>选择封面（对齐工作流编辑：优先图库缩略图列表，弹窗底部可从电脑选择）→ 复制到工作空间 covers\&lt;id&gt;.&lt;ext&gt;。</summary>
    [RelayCommand]
    private async Task PickCoverAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            var settings = _service.GetRequiredService<ComfySettings>();
            var dbFactory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
            var pickerVm = new ThumbnailPickerModel(settings, dbFactory, _local, _noticeService, _log);
            var viewService = _service.GetRequiredService<IBaseViewService>();
            if (!viewService.TryCreateView(pickerVm, out var pickerView, "ThumbnailPicker"))
            {
                _log.Warn(_local["ThumbnailPickerViewNotRegistered"], "PromptLibrary");
                return;
            }

            var host = new SukiMessageBoxHost
            {
                Content = pickerView,
                IconPreset = null,
                Width = 760,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
            };

            // 底部操作区：[从电脑选择] [确定]（确定随选中启用；Esc = 取消）
            var pickBtn = CreatePickerButton(SukiMessageBoxResult.Continue, _local["PickFromComputer"], pickerVm.PickFromComputerCommand);
            var confirmBtn = CreatePickerButton(SukiMessageBoxResult.OK, _local["Ok"], pickerVm.ConfirmCommand);
            confirmBtn.IsEnabled = false;
            pickerVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThumbnailPickerModel.SelectedItem))
                    confirmBtn.IsEnabled = pickerVm.SelectedItem != null;
            };
            host.ActionButtonsSource = new AvaloniaList<Avalonia.Controls.Button> { pickBtn, confirmBtn };

            var options = new SukiMessageBoxOptions
            {
                Title = _local["ChooseCoverImageTitle"],
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            if (owner is SukiWindow sukiOwner)
            {
                options = options with
                {
                    BackgroundAnimationEnabled = sukiOwner.BackgroundAnimationEnabled,
                    BackgroundForceSoftwareRendering = sukiOwner.BackgroundForceSoftwareRendering,
                    BackgroundShaderCode = sukiOwner.BackgroundShaderCode,
                    BackgroundShaderFile = sukiOwner.BackgroundShaderFile,
                    BackgroundStyle = sukiOwner.BackgroundStyle,
                    BackgroundTransitionTime = sukiOwner.BackgroundTransitionTime,
                    BackgroundTransitionsEnabled = sukiOwner.BackgroundTransitionsEnabled,
                };
            }

            var window = SukiMessageBox.CreateMessageBoxWindow(options);
            window.Content = host;
            pickerVm.RequestClose += () => window.Close();
            window.KeyUp += (_, e) =>
            {
                if (e.Key == Key.Escape) window.Close();
            };
            await window.ShowDialog(owner);

            if (pickerVm.Result == ThumbnailPickerModel.PickerResult.Cancelled || string.IsNullOrEmpty(pickerVm.SelectedPath))
                return;
            await ApplyCoverFileAsync(pickerVm.SelectedPath);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptSetCoverFailed"], ex.Message);
            _log.Error(_local["PromptSetCoverFailedLog"], "PromptLibrary", ex);
        }
    }

    /// <summary>底部操作按钮（点击执行命令；RequestClose 由 VM 内触发关闭）。</summary>
    private static Avalonia.Controls.Button CreatePickerButton(
        SukiMessageBoxResult result, string text, CommunityToolkit.Mvvm.Input.IRelayCommand command)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(result, text);
        button.Click += (_, _) => command.Execute(null);
        return button;
    }

    /// <summary>把所选图片复制到工作空间 covers\&lt;id&gt;.&lt;ext&gt; 并刷新预览（对齐 importImageFile）。</summary>
    private async Task ApplyCoverFileAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }.Contains(ext))
        {
            StatusMessage = _local["PromptCoverTypeError"];
            return;
        }
        var destName = $"{_promptId}{ext}";
        var dest = Path.Combine(_workspace.CoversDir, destName);
        File.Copy(path, dest, overwrite: true);
        Cover = destName;
        await LoadCoverAsync();
        StatusMessage = _local["PromptCoverUpdated"];
    }

    /// <summary>即时新建全局标签（对齐 PromptMaster allow-create：输入名字 → 全局标签池新建 → 自动选中）。</summary>
    [RelayCommand]
    private async Task AddNewTagAsync()
    {
        var name = NewTagName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            StatusMessage = _local["PromptTagNameRequired"];
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
            StatusMessage = _local["PromptTagCreated"];
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>标签勾选变化 → 同步 SelectedTagIds（Avalonia CheckBox 无 Checked/Unchecked 事件绑定，走 Command）。</summary>
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
    private async Task CopyPositiveAsync()
    {
        await CopyTextAsync(Positive, _local["CopyPositive"]);
    }

    [RelayCommand]
    private async Task CopyNegativeAsync()
    {
        await CopyTextAsync(Negative, _local["CopyNegative"]);
    }

    private async Task CopyTextAsync(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = _local["CopyEmpty"];
            return;
        }
        // 项目统一剪贴板服务（Avalonia 12 IClipboard 无 SetTextAsync，由服务封装）
        _service.GetRequiredService<IBaseClipboardService>().CopyToClipboard(text);
        StatusMessage = string.Format(_local["CopiedSuffix"], what);
        await Task.CompletedTask;
    }
}
