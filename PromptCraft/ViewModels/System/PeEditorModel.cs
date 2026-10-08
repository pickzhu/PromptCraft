using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.Models.Inference;
using PromptCraft.Service;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 提示词工程「新建 / 编辑」对话框 ViewModel（与词库添加提示词弹窗同构：独立 VM + SukiMessageBox 宿主）。
/// 保存走 <see cref="PmPromptEngineeringService.SaveProfile"/>；成功触发 <see cref="Saved"/> 由宿主关窗并刷新。
/// </summary>
public partial class PeEditorModel : ViewModelBase
{
    private readonly IServiceProvider _service;
    private readonly PmPromptEngineeringService _peService;
    private readonly string? _editingId;

    /// <summary>弹窗标题（新建 / 编辑）。</summary>
    [ObservableProperty] private string _editorTitle = "";

    [ObservableProperty] private bool _isEditorKindExpand = true;
    [ObservableProperty] private bool _isEditorKindReverse;
    [ObservableProperty] private bool _isEditorKindTrain;

    private string _editorKind = "expand";

    [ObservableProperty] private string _editorName = "";
    [ObservableProperty] private string _editorSystemPrompt = "";
    [ObservableProperty] private string _editorUserPrompt = "";
    [ObservableProperty] private string _editorDescription = "";
    [ObservableProperty] private OutputFormatOption? _editorOutputFormatOption;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>保存成功（宿主订阅：刷新列表并关闭弹窗；携带已保存工程用于分类联动）。</summary>
    public event Action<PromptEngineeringProfile>? Saved;

    public string EditorKind => _editorKind;

    /// <summary>是否有校验/保存错误提示（宿主内容区底部红字）。</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public PeEditorModel(
        ILocalizer localizer,
        IBaseNotice notice,
        IServiceProvider service,
        PmPromptEngineeringService peService,
        string defaultKind,
        PromptEngineeringProfile? existing)
        : base(localizer, notice)
    {
        _service = service;
        _peService = peService;
        if (existing != null)
        {
            _editingId = existing.Id;
            EditorTitle = _local["PeEditorEditTitle"];
            _editorKind = existing.Kind is "reverse" or "train" ? existing.Kind : "expand";
            SyncEditorKindBools();
            EditorName = existing.Name ?? "";
            EditorSystemPrompt = existing.SystemPrompt ?? "";
            EditorUserPrompt = existing.UserPromptTemplate ?? "";
            EditorDescription = existing.Description ?? "";
            EditorOutputFormatOption = EditorOutputFormatOptions
                .FirstOrDefault(o => o.Id == (string.IsNullOrWhiteSpace(existing.OutputFormat) ? "prose" : existing.OutputFormat));
        }
        else
        {
            _editingId = null;
            EditorTitle = _local["PeEditorCreateTitle"];
            _editorKind = defaultKind is "reverse" or "train" ? defaultKind : "expand";
            SyncEditorKindBools();
            EditorOutputFormatOption = null;
        }
        FixEditorOutputFormat();
    }

    /// <summary>编辑器输出格式选项（随分类联动）。</summary>
    public IReadOnlyList<OutputFormatOption> EditorOutputFormatOptions
        => PmPromptEngineeringService.GetOutputFormatOptions(EditorKind)
            .Select(o => new OutputFormatOption(o.Id, o.Label))
            .ToList();

    public string EditorSystemPlaceholder
        => EditorKind == "reverse" ? _local["PeSysPromptExampleReverse"]
        : EditorKind == "train" ? _local["PeSysPromptExampleTrain"]
        : _local["PeSysPromptExampleExpand"];

    public string EditorUserPlaceholder
        => EditorKind == "reverse" ? _local["PeExtraPlaceholderReverse"]
        : EditorKind == "train" ? _local["PeExtraPlaceholderTrain"]
        : _local["PeExtraPlaceholderExpand"];

    partial void OnIsEditorKindExpandChanged(bool value)
    {
        if (value) SelectEditorKind("expand");
    }

    partial void OnIsEditorKindReverseChanged(bool value)
    {
        if (value) SelectEditorKind("reverse");
    }

    partial void OnIsEditorKindTrainChanged(bool value)
    {
        if (value) SelectEditorKind("train");
    }

    /// <summary>切换编辑器分类：输出格式联动 + placeholder 更新（对齐 PromptMaster）。</summary>
    public void SelectEditorKind(string kind)
    {
        if (_editorKind == kind)
            return;
        _editorKind = kind;
        SyncEditorKindBools();
        OnPropertyChanged(nameof(EditorOutputFormatOptions));
        OnPropertyChanged(nameof(EditorSystemPlaceholder));
        OnPropertyChanged(nameof(EditorUserPlaceholder));
        FixEditorOutputFormat();
    }

    private void SyncEditorKindBools()
    {
        IsEditorKindExpand = _editorKind == "expand";
        IsEditorKindReverse = _editorKind == "reverse";
        IsEditorKindTrain = _editorKind == "train";
    }

    /// <summary>输出格式随分类联动（对齐 getOutputFormatOptions(kind)）；非法/缺失回退首个。</summary>
    private void FixEditorOutputFormat()
    {
        if (EditorOutputFormatOptions.All(o => o.Id != EditorOutputFormatOption?.Id))
            EditorOutputFormatOption = EditorOutputFormatOptions.FirstOrDefault();
    }

    /// <summary>保存：校验 + 调服务写入，成功触发 <see cref="Saved"/> 并埋操作日志。</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorName))
        {
            StatusMessage = _local["PeNameRequired"];
            return;
        }
        if (string.IsNullOrWhiteSpace(EditorSystemPrompt))
        {
            StatusMessage = _local["PeSystemPromptRequired"];
            return;
        }
        IsSaving = true;
        try
        {
            var profile = new PromptEngineeringProfile
            {
                Id = _editingId ?? "",
                Kind = EditorKind,
                Name = EditorName.Trim(),
                Category = EditorKind == "reverse" ? _local["KindReverse"] : EditorKind == "train" ? _local["KindTrain"] : _local["KindExpand"],
                Description = EditorDescription ?? "",
                SystemPrompt = EditorSystemPrompt,
                UserPromptTemplate = EditorUserPrompt ?? "",
                OutputFormat = EditorOutputFormatOption?.Id ?? "prose",
                Enabled = true,
            };
            var saved = await Task.Run(() => _peService.SaveProfile(profile));
            LogService.Instance.Info(string.Format(string.IsNullOrEmpty(_editingId) ? _local["PeSaveLog"] : _local["PeUpdateLog"], saved.Name, saved.Kind == "reverse" ? _local["KindReverse"] : saved.Kind == "train" ? _local["KindTrain"] : _local["KindExpand"]), "PromptEngineering");
            Saved?.Invoke(saved);
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
}
