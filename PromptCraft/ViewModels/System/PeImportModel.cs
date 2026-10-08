using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.Models.Inference;
using PromptCraft.Service;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer.Abstractions;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 提示词工程「JSON 导入」对话框 ViewModel（与词库添加提示词弹窗同构：独立 VM + SukiMessageBox 宿主）。
/// 校验走 <see cref="PmPromptEngineeringService.ValidateImportFile"/>；导入走 <see cref="PmPromptEngineeringService.ImportProfile"/>。
/// 成功触发 <see cref="Imported"/> 由宿主关窗并刷新。
/// </summary>
public partial class PeImportModel : ViewModelBase
{
    private readonly IServiceProvider _service;
    private readonly PmPromptEngineeringService _peService;
    private string? _importFilePath;

    [ObservableProperty] private string _importFileName = "";
    [ObservableProperty] private string _importMessage = "";
    [ObservableProperty] private bool _importCanSubmit;
    [ObservableProperty] private bool _isImporting;

    /// <summary>导入成功（宿主订阅：刷新列表并关闭弹窗；携带已导入工程用于分类联动）。</summary>
    public event Action<PromptEngineeringProfile>? Imported;

    public PeImportModel(
        ILocalizer localizer,
        IBaseNotice notice,
        IServiceProvider service,
        PmPromptEngineeringService peService)
        : base(localizer, notice)
    {
        _service = service;
        _peService = peService;
    }

    public string ImportHint => _local["PeImportHint"];

    [RelayCommand]
    private async Task PickImportFileAsync()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window == null) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _local["PePickImportTitle"],
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(_local["PeJsonFileType"]) { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        SetImportFile(files[0].TryGetLocalPath() ?? files[0].Path.AbsolutePath);
    }

    /// <summary>设置导入文件并校验（validateImportFile）。拖拽与文件选择共用。</summary>
    public void SetImportFile(string path)
    {
        _importFilePath = path;
        ImportFilePathChanged();
    }

    private void ImportFilePathChanged()
    {
        var (valid, fileName, profileName, _, message) = _peService.ValidateImportFile(_importFilePath);
        ImportFileName = fileName;
        ImportMessage = valid ? string.Format(_local["PeImportableFormat"], profileName) : message;
        ImportCanSubmit = valid;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (string.IsNullOrEmpty(_importFilePath) || !ImportCanSubmit)
            return;
        IsImporting = true;
        try
        {
            var profile = await Task.Run(() => _peService.ImportProfile(_importFilePath));
            var kindLabel = profile.Kind == "reverse" ? _local["KindReverse"] : profile.Kind == "train" ? _local["KindTrain"] : _local["KindExpand"];
            LogService.Instance.Info(string.Format(_local["PeImportLog"], profile.Name, kindLabel), "PromptEngineering");
            Imported?.Invoke(profile);
        }
        catch (Exception ex)
        {
            ImportMessage = ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }
}
