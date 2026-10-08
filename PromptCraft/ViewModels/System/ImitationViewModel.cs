using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Service;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 仿写页（单次生成）：原始提示词 + 仿写要求 → 新提示词。
/// 复用扩写默认模型；输出格式下拉复用扩写 6 类；参数经 pm_imitation_* 记忆。
/// </summary>
public partial class ImitationViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private readonly IServiceProvider _services;
    private IExpandSettingsStore? _settings;
    private bool _settingsLoaded;

    // 参数记忆 key
    private const string KeyProvider = "pm_imitation_provider";
    private const string KeyModel = "pm_imitation_model";
    private const string KeyFormat = "pm_imitation_format";
    private const string KeySource = "pm_imitation_source";
    private const string KeyRequirement = "pm_imitation_requirement";
    private const string KeyOutputLang = "pm_imitation_output_lang";

    [ObservableProperty] private string _sourcePrompt = "";
    [ObservableProperty] private string _requirement = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _selectedOutputLang = "zh";
    [ObservableProperty] private FormatOption? _selectedFormat;
    [ObservableProperty] private ProviderConfig? _selectedProvider;
    [ObservableProperty] private ProviderModel? _selectedModel;

    public sealed record FormatOption(string Id, string Name);

    /// <summary>输出格式（复用扩写 6 类：prose/sd_tags/danbooru_tags/structured_md/structured_json/minimax）。</summary>
    public static readonly string[] OutputFormatIds =
        { "prose", "sd_tags", "danbooru_tags", "structured_md", "structured_json", "minimax" };

    public AvaloniaList<ProviderConfig> Providers { get; } = new();
    public AvaloniaList<ProviderModel> FilteredModels { get; } = new();

    /// <summary>按所选服务商过滤的模型列表。</summary>
    private void RefreshFilteredModels()
    {
        FilteredModels.Clear();
        if (SelectedProvider == null) return;
        foreach (var m in SelectedProvider.Models)
            FilteredModels.Add(m);
    }

    /// <summary>服务商无可用模型时的提示文案。</summary>
    public string NoModelsText => string.Format(_local["NoModelsText"], SelectedProvider?.Name ?? "");
    public bool HasExpandModels => FilteredModels.Count > 0;

    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyProvider, value?.Id ?? "");
        RefreshFilteredModels();
        OnPropertyChanged(nameof(HasExpandModels));
        OnPropertyChanged(nameof(NoModelsText));
        if (SelectedModel == null && FilteredModels.Count > 0) SelectedModel = FilteredModels[0];
    }

    partial void OnSelectedModelChanged(ProviderModel? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyModel, value?.ModelName ?? "");
    }

    /// <summary>去配置（跳服务商设置页）。</summary>
    public void GoConfigureProvider()
    {
        _noticeService.Publish(EventNameConst.SystemNavigatePageEvent, typeof(SettingModel));
    }
    public IRelayCommand GoSettingsCommand => new RelayCommand(GoConfigureProvider);

    public IReadOnlyList<FormatOption> FormatItems =>
        OutputFormatIds.Select(id => new FormatOption(id, _local[$"ExpandRuleName_{id}"] ?? id)).ToList();

    partial void OnSelectedFormatChanged(FormatOption? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyFormat, value?.Id ?? "");
    }

    partial void OnSourcePromptChanged(string value)
    {
        if (_settingsLoaded) _settings?.Set(KeySource, value ?? "");
    }

    partial void OnRequirementChanged(string value)
    {
        if (_settingsLoaded) _settings?.Set(KeyRequirement, value ?? "");
    }

    // 输出语言 radio
    public bool IsLangZh
    {
        get => SelectedOutputLang == "zh";
        set { if (value) SelectedOutputLang = "zh"; }
    }
    public bool IsLangEn
    {
        get => SelectedOutputLang == "en";
        set { if (value) SelectedOutputLang = "en"; }
    }

    partial void OnSelectedOutputLangChanged(string value)
    {
        OnPropertyChanged(nameof(IsLangZh));
        OnPropertyChanged(nameof(IsLangEn));
        if (_settingsLoaded) _settings?.Set(KeyOutputLang, value);
    }

    public bool HasResult => !string.IsNullOrWhiteSpace(Result);
    public string ResultCharCountText => string.Format(_local["ResultCharCountText"], Result.Length);

    partial void OnResultChanged(string value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultCharCountText));
    }

    public string Title => _local["ImitationTitle"];

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(FormatItems));
    }

    public ImitationViewModel(ILocalizer localizer, IBaseNotice baseNotice, IServiceProvider services)
        : base(localizer, baseNotice)
    {
        _displayName = "IMITATION";
        _icon = MaterialIconKind.AutoFixHigh;
        _index = 41;
        _sideMenu = true;
        _services = services;
        _settings = services.GetRequiredService<IExpandSettingsStore>();
        _selectedFormat = FormatItems[0];
        _noticeService.Subscribe(EventNameConst.ProviderChangedEvent, _ => _ = ReloadProvidersAsync());
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await _settings!.LoadAsync();

            var format = _settings.Get(KeyFormat);
            if (!string.IsNullOrEmpty(format))
            {
                var f = FormatItems.FirstOrDefault(x => x.Id == format);
                if (f != null) SelectedFormat = f;
            }

            var lang = _settings.Get(KeyOutputLang);
            if (lang == "en") SelectedOutputLang = "en";
            else if (lang == "zh") SelectedOutputLang = "zh";

            var source = _settings.Get(KeySource);
            if (!string.IsNullOrEmpty(source)) SourcePrompt = source;
            var req = _settings.Get(KeyRequirement);
            if (!string.IsNullOrEmpty(req)) Requirement = req;

            _settingsLoaded = true;

            await ReloadProvidersAsync();
        }
        catch { }
    }

    private async Task ReloadProvidersAsync()
    {
        try
        {
            var svc = _services.GetRequiredService<IProviderService>();
            var all = await svc.GetAllAsync(default);
            Providers.Clear();
            foreach (var p in all) Providers.Add(p);

            var provId = _settings?.Get(KeyProvider);
            var modelName = _settings?.Get(KeyModel);
            ProviderConfig? selProv = null;
            ProviderModel? selModel = null;
            if (!string.IsNullOrEmpty(provId) && !string.IsNullOrEmpty(modelName))
            {
                selProv = Providers.FirstOrDefault(p => p.Id == provId);
                selModel = selProv?.Models.FirstOrDefault(m => m.ModelName == modelName);
                if (selModel == null) selProv = null;
            }
            if (selProv == null || selModel == null)
            {
                foreach (var p in Providers)
                {
                    var m = p.Models.FirstOrDefault(x => x.IsDefaultExpand);
                    if (m != null) { selProv = p; selModel = m; break; }
                }
            }
            SelectedProvider = selProv ?? Providers.FirstOrDefault();
            SelectedModel = selModel ?? SelectedProvider?.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["LoadProvidersFailedLog"], ex.Message), "Imitation");
        }
    }

    private CancellationTokenSource? _cts;

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(SourcePrompt))
        {
            ShowToast(_local["ImitationSourceRequired"] ?? "请先输入原始提示词", "");
            return;
        }
        if (string.IsNullOrWhiteSpace(Requirement))
        {
            ShowToast(_local["ImitationRequirementRequired"] ?? "请先输入仿写要求", "");
            return;
        }

        IsBusy = true;
        ErrorMessage = "";
        Result = "";
        _cts = new CancellationTokenSource();
        LogService.Instance.Info($"仿写开始：模型 {SelectedModel?.ModelName ?? ""}", "Imitation");
        try
        {
            var svc = _services.GetRequiredService<IImitationService>();
            Result = await svc.ImitateAsync(new PromptCraft.Models.Inference.ImitationRequest
            {
                SourcePrompt = SourcePrompt ?? "",
                Requirement = Requirement ?? "",
                OutputLang = SelectedOutputLang,
                FormatId = SelectedFormat?.Id,
            }, SelectedProvider, SelectedModel, _cts.Token);
            if (!string.IsNullOrWhiteSpace(Result))
                ShowToast(_local["ImitationDone"] ?? "仿写完成", "");
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = _local["ExpandCancelled"];
            ShowToast(_local["ExpandStopped"] ?? "已停止", "");
            LogService.Instance.Info(_local["ExpandStoppedLog"] ?? "仿写已停止", "Imitation");
        }
        catch (Exception ex)
        {
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? (_local["ImitationFailed"] ?? "仿写失败") : ex.Message;
            ErrorMessage = msg;
            ShowToast(msg, "");
            LogService.Instance.Error(string.Format(_local["ExpandFailedLog"], msg), "Imitation", ex);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task CopyResult()
    {
        if (string.IsNullOrWhiteSpace(Result))
        {
            ShowToast(_local["PromptEmpty"] ?? "提示词为空", "");
            return;
        }
        try
        {
            var clip = _services.GetRequiredService<IBaseClipboardService>();
            clip.CopyToClipboard(Result);
            ShowToast(_local["ExpandCopied"] ?? "已复制", "");
        }
        catch (Exception ex)
        {
            ShowToast(_local["CopyFailed"] ?? "复制失败", "");
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["CopyExpandFailedLog"], ex.Message), "Imitation");
        }
    }

    [RelayCommand]
    private async Task SaveToLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(Result))
        {
            ShowToast(_local["NothingToSave"] ?? "没有可保存的内容", "");
            return;
        }
        try
        {
            await PromptCraft.ViewModels.PromptLibrary.PromptEditDialogOpener.OpenForExpandAsync(_services, Result, Requirement ?? "");
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Error(_local["OpenSaveDialogFailedLog"] ?? "打开保存对话框失败", "Imitation", ex);
        }
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
        catch { }
    }
}
