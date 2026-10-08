using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.Inference;
using PromptCraft.Service;
using PromptCraft.Service.Inference.Reverse;
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

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 提示词工程管理页（1:1 对齐 PromptMaster PromptEngineering 页）：
/// 分类 Tab（扩写/反推/训练打标）、输出格式+搜索筛选、表格（名称/输出格式/分类/来源/备注/启用/操作）、
/// 新建/编辑/导出/删除/JSON 导入；自定义 PE 持久化到 comfyui.db（pe_profiles 表）。
/// </summary>
public partial class PromptEngineeringViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private readonly IServiceProvider _services;
    private readonly PmPromptEngineeringService _peService;
    private readonly IWorkspaceService _workspace;

    private List<PromptEngineeringProfile> _loaded = new();

    public PromptEngineeringViewModel(ILocalizer localizer, IBaseNotice baseNotice, IServiceProvider services)
        : base(localizer, baseNotice)
    {
        _displayName = "PROMPTENGINEERING";
        _icon = MaterialIconKind.Cogs;
        _index = 70;
        _sideMenu = true;
        _services = services;
        _workspace = services.GetRequiredService<IWorkspaceService>();
        _peService = new PmPromptEngineeringService(
            workspaceRoot: string.IsNullOrEmpty(_workspace.Root) ? null : _workspace.Root,
            dbFactory: services.GetRequiredService<IDbContextFactory<ComfyDbContext>>());

        KindTabs = new List<KindTab>
        {
            new("expand", _local["KindExpand"]),
            new("reverse", _local["KindReverse"]),
            new("train", _local["KindTrain"]),
        };
        _selectedKind = "expand";
        _selectedOutputFormatOption = new("", _local["PeAllFormats"]);
        _ = LoadAsync();
    }

    public string Title => _local["PromptEngineeringTitle"] ?? "";

    // ==================== 分类 Tab ====================

    public IReadOnlyList<KindTab> KindTabs { get; }

    [ObservableProperty]
    private bool _isKindExpand = true;

    [ObservableProperty]
    private bool _isKindReverse;

    [ObservableProperty]
    private bool _isKindTrain;

    private string _selectedKind = "expand";

    public string SelectedKind => _selectedKind;

    partial void OnIsKindExpandChanged(bool value)
    {
        if (value) SelectKind("expand");
    }

    partial void OnIsKindReverseChanged(bool value)
    {
        if (value) SelectKind("reverse");
    }

    partial void OnIsKindTrainChanged(bool value)
    {
        if (value) SelectKind("train");
    }

    /// <summary>切换分类 Tab：清筛选重拉（对齐 PromptMaster）。</summary>
    public void SelectKind(string kind)
    {
        if (_selectedKind == kind)
            return;
        _selectedKind = kind;
        SyncKindBools();
        ClearFilter();
        _ = LoadAsync();
    }

    private void SyncKindBools()
    {
        IsKindExpand = _selectedKind == "expand";
        IsKindReverse = _selectedKind == "reverse";
        IsKindTrain = _selectedKind == "train";
    }

    // ==================== 筛选 ====================

    public IReadOnlyList<OutputFormatOption> OutputFormatOptions
    {
        get
        {
            var list = new List<OutputFormatOption> { new("", _local["PeAllFormats"]) };
            list.AddRange(PmPromptEngineeringService.GetOutputFormatOptions(SelectedKind)
                .Select(o => new OutputFormatOption(o.Id, o.Label)));
            return list;
        }
    }

    [ObservableProperty]
    private OutputFormatOption? _selectedOutputFormatOption = new("", "");

    partial void OnSelectedOutputFormatOptionChanged(OutputFormatOption? value) => ApplyFilter();

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public bool HasFilter => !string.IsNullOrEmpty(SelectedOutputFormatOption?.Id) || !string.IsNullOrWhiteSpace(SearchText);

    public string CountLabel => string.Format(_local["PeCountLabel"], Rows.Count);

    public ObservableCollection<PeRowItem> Rows { get; } = new();

    [RelayCommand]
    private void ClearFilter()
    {
        SelectedOutputFormatOption = new("", _local["PeAllFormats"]);
        SearchText = "";
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(OutputFormatOptions));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var query = _loaded.AsEnumerable();
        if (!string.IsNullOrEmpty(SelectedOutputFormatOption?.Id))
            query = query.Where(p => p.OutputFormat == SelectedOutputFormatOption!.Id);
        var keyword = SearchText?.Trim() ?? "";
        if (keyword.Length > 0)
        {
            query = query.Where(p =>
                (p.Name ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (p.Description ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || PmPromptEngineeringService.GetOutputFormatLabel(p.OutputFormat).Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var p in query)
        {
            Rows.Add(new PeRowItem
            {
                Id = p.Id,
                // 名称/备注直接显示原文：内置条目 Name/Description 已是中文原文，
                // _local["PeName_xxx"] 查不到 key 时返回 key 本身（导致表格显示 PeName_pe_expand_…）
                Name = p.Name ?? "",
                OutputFormatLabel = PmPromptEngineeringService.GetOutputFormatLabel(p.OutputFormat),
                Category = p.Kind == "expand" ? _local["KindExpand"] : p.Kind == "train" ? _local["KindTrain"] : _local["KindReverse"],
                Builtin = p.Builtin,
                Description = p.Description ?? "",
                Kind = p.Kind,
                Enabled = p.Enabled,
                IsAlt = Rows.Count % 2 == 1,
            });
        }
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(HasFilter));
    }

    // ==================== 列表加载 ====================

    private async Task LoadAsync()
    {
        try
        {
            _loaded = await Task.Run(() => _peService.ListProfiles(SelectedKind));
        }
        catch (Exception ex)
        {
            _loaded = new List<PromptEngineeringProfile>();
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["PeLoadFailedLog"], ex.Message), "PromptEngineering");
        }
        ApplyFilter();
    }

    // ==================== 新建 / 编辑 ====================

    [RelayCommand]
    private void New()
    {
        if (!EnsureWorkspaceConfigured()) return;
        _ = PeDialogOpener.OpenEditorAsync(_services, null, SelectedKind, OnEditorSaved);
    }

    [RelayCommand]
    private void Edit(PeRowItem? row)
    {
        if (row == null) return;
        if (row.Builtin)
        {
            ShowToast(_local["PeBuiltinNotEditable"], _local["PeBuiltinNotEditableHint"]);
            return;
        }
        var p = _peService.GetProfile(row.Id);
        if (p == null)
        {
            ShowToast(_local["PeNotFound"], "");
            return;
        }
        _ = PeDialogOpener.OpenEditorAsync(_services, p, SelectedKind, OnEditorSaved);
    }

    /// <summary>编辑器保存成功回调：发布变更事件 + 分类联动/重载列表（对齐 PromptMaster 分类变化切 Tab）。</summary>
    private void OnEditorSaved(PromptEngineeringProfile saved)
    {
        _noticeService.Publish(EventNameConst.PromptEngineeringChangedEvent, null); // 扩写/反推页刷新 PE 下拉
        if (saved.Kind != SelectedKind)
            SelectKind(saved.Kind);
        else
            _ = LoadAsync();
    }

    // ==================== 启用开关 ====================

    [RelayCommand]
    private async Task ToggleEnabledAsync(PeRowItem? row)
    {
        if (row == null) return;
        var prev = row.Enabled;
        try
        {
            await Task.Run(() => _peService.SetProfileEnabled(row.Id, row.Enabled));
            // 同步内存列表，保证切换筛选/搜索后显示状态不回退（服务层已落库）
            var cached = _loaded.FirstOrDefault(x => x.Id == row.Id);
            if (cached != null)
                cached.Enabled = row.Enabled;
            LogService.Instance.Info(string.Format(_local["PeToggleEnabledLog"], row.Name, row.Enabled ? _local["PeLogEnabled"] : _local["PeLogDisabled"]), "PromptEngineering");
            _noticeService.Publish(EventNameConst.PromptEngineeringChangedEvent, null); // 扩写/反推页刷新（被禁用项不再出现）
        }
        catch (Exception ex)
        {
            row.Enabled = prev; // 失败回滚 + 报错
            ShowToast(_local["PeOperationFailed"], ex.Message);
        }
    }

    // ==================== 删除 ====================

    [RelayCommand]
    private async Task DeleteAsync(PeRowItem? row)
    {
        if (row == null) return;
        if (row.Builtin)
        {
            ShowToast(_local["PeBuiltinNotDeletable"], _local["PeBuiltinNotDeletableHint"]);
            return;
        }
        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = string.Format(_local["PeDeleteConfirmFormat"], row.Name),
                    Margin = new Thickness(4),
                    TextWrapping = TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["PeDeleteTitle"], MinWidth = 340 });
        if (confirm is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK))
            return;
        try
        {
            await Task.Run(() => _peService.DeleteProfile(row.Id));
            ShowToast(_local["PeDeleted"], "");
            LogService.Instance.Info(string.Format(_local["PeDeleteLog"], row.Name), "PromptEngineering");
            _noticeService.Publish(EventNameConst.PromptEngineeringChangedEvent, null); // 扩写/反推页刷新 PE 下拉
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ShowToast(_local["PeDeleteFailed"], ex.Message);
        }
    }

    // ==================== 导出 ====================

    [RelayCommand]
    private async Task ExportAsync(PeRowItem? row)
    {
        if (row == null) return;
        if (row.Builtin)
        {
            ShowToast(_local["PeBuiltinNotExportable"], "");
            return;
        }
        try
        {
            var json = await Task.Run(() => _peService.ExportProfile(row.Id));
            var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window == null) return;
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _local["PeExportTitle"],
                SuggestedFileName = SafeExportFileName(row.Name),
                DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType(_local["PeJsonFileType"]) { Patterns = new[] { "*.json" } } },
            });
            if (file == null) return;
            var path = file.TryGetLocalPath() ?? file.Path.AbsolutePath;
            await File.WriteAllTextAsync(path, json);
            ShowToast(string.Format(_local["PeExportedFormat"], row.Name), path);
            LogService.Instance.Info(string.Format(_local["PeExportLog"], row.Name, path), "PromptEngineering");
        }
        catch (Exception ex)
        {
            ShowToast(_local["PeExportFailed"], ex.Message);
        }
    }

    /// <summary>文件名安全化（safeExportFileName）：非法字符替换为 _，截断 80 字符。</summary>
    private static string SafeExportFileName(string? name)
    {
        var s = (name ?? "profile")
            .Replace('<', '_').Replace('>', '_').Replace(':', '_')
            .Replace('"', '_').Replace('/', '_').Replace('\\', '_')
            .Replace('|', '_').Replace('?', '_').Replace('*', '_')
            .Replace(' ', '_');
        while (s.Contains("__"))
            s = s.Replace("__", "_");
        s = s.Length > 80 ? s[..80] : s;
        return s.Length > 0 ? $"prompt-engineering-{s}.json" : "prompt-engineering-profile.json";
    }

    // ==================== 导入 ====================

    [RelayCommand]
    private void OpenImport()
    {
        if (!EnsureWorkspaceConfigured()) return;
        _ = PeDialogOpener.OpenImportAsync(_services, OnImported);
    }

    /// <summary>导入成功回调：发布变更事件 + 分类联动/重载列表（对齐 PromptMaster 分类变化切 Tab）。</summary>
    private void OnImported(PromptEngineeringProfile profile)
    {
        _noticeService.Publish(EventNameConst.PromptEngineeringChangedEvent, null); // 扩写/反推页刷新 PE 下拉
        if (profile.Kind != SelectedKind)
            SelectKind(profile.Kind);
        else
            _ = LoadAsync();
    }

    // ==================== 公共 ====================

    private bool EnsureWorkspaceConfigured()
    {
        if (!string.IsNullOrEmpty(_workspace.Root))
            return true;
        ShowWarning(_local["PeWorkspaceRequired"], _local["PeWorkspaceRequiredHint"]);
        return false;
    }

    private void ShowToast(string title, string content)
    {
        try
        {
            var toastManager = _services.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
            if (toastManager == null) return;
            var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3);
            toast.Queue();
        }
        catch
        {
            // toast 失败不影响主流程
        }
    }

    private void ShowWarning(string title, string content)
    {
        // SukiUI 无独立 warning toast 构建器，与 info 同构（内容提示醒目即可）
        ShowToast(title, content);
    }

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(Title));
        base.OnSystemLangueChanged(data);
    }
}

/// <summary>分类 Tab 项（扩写 / 反推 / 训练打标）。</summary>
public sealed record KindTab(string Kind, string Label);

/// <summary>输出格式下拉项（Id 为空表示「全部格式」）。</summary>
public sealed record OutputFormatOption(string Id, string Label);

/// <summary>管理页表格行。</summary>
public partial class PeRowItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string OutputFormatLabel { get; init; } = "";
    public string Category { get; init; } = "";
    public bool Builtin { get; init; }
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";

    [ObservableProperty]
    private bool _enabled;

    /// <summary>交替行背景标记（表格隔行变色）。</summary>
    public bool IsAlt { get; init; }
}
