using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 提示词工程「新建/编辑」与「JSON 导入」对话框的统一打开入口。
/// 宿主为 SukiMessageBox 弹窗（与词库添加提示词弹窗 PromptEditDialogOpener 完全同构），
/// 保存/导入成功触发 Saved/Imported 并关闭；foot 按钮位于宿主底部。
/// </summary>
public static class PeDialogOpener
{
    /// <param name="existing">null = 新建</param>
    /// <param name="defaultKind">新建时的默认分类（取当前选中 Tab）</param>
    /// <param name="onSaved">保存成功回调（宿主刷新列表 + 发布变更事件）</param>
    public static async Task OpenEditorAsync(
        IServiceProvider service,
        PromptEngineeringProfile? existing,
        string defaultKind,
        Action<PromptEngineeringProfile>? onSaved = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            var local = service.GetRequiredService<ILocalizer>();
            var notice = service.GetRequiredService<IBaseNotice>();
            var viewService = service.GetRequiredService<IBaseViewService>();
            var peService = CreatePeService(service);

            var editVm = new PeEditorModel(local, notice, service, peService, defaultKind, existing);
            if (!viewService.TryCreateView(editVm, out var editView, "PeEditor")) return;

            var host = new SukiMessageBoxHost
            {
                Content = editView,
                IconPreset = null,
                Width = 720,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
                // 宿主 foot：[保存（执行 SaveCommand，成功 Saved → 关窗）] [关闭]
                ActionButtonsSource = new AvaloniaList<Avalonia.Controls.Button>
                {
                    CreateActionButton(SukiMessageBoxResult.OK, local["Save"] ?? "", editVm.SaveCommand),
                    CreateCloseButton(local),
                },
            };
            var options = BuildOptions(local["PeEditorCreateTitle"] ?? "", owner);
            var window = SukiMessageBox.CreateMessageBoxWindow(options);
            editVm.Saved += p =>
            {
                onSaved?.Invoke(p);
                window.Close();
            };
            WireFootButtons(window, host);
            window.Content = host;
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Error("Open PE editor dialog failed", "PromptEngineering", ex);
        }
    }

    /// <summary>打开「JSON 导入」对话框（foot：[导入] [关闭]，导入按钮随可导入状态启用）。</summary>
    public static async Task OpenImportAsync(
        IServiceProvider service,
        Action<PromptEngineeringProfile>? onImported = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            var local = service.GetRequiredService<ILocalizer>();
            var notice = service.GetRequiredService<IBaseNotice>();
            var viewService = service.GetRequiredService<IBaseViewService>();
            var peService = CreatePeService(service);

            var importVm = new PeImportModel(local, notice, service, peService);
            if (!viewService.TryCreateView(importVm, out var importView, "PeImport")) return;

            var importButton = CreateActionButton(SukiMessageBoxResult.OK, local["PeImportButton"] ?? "", importVm.ImportCommand);
            importButton.IsEnabled = false;
            importVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PeImportModel.ImportCanSubmit))
                    importButton.IsEnabled = importVm.ImportCanSubmit;
            };

            var host = new SukiMessageBoxHost
            {
                Content = importView,
                IconPreset = null,
                Width = 560,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
                ActionButtonsSource = new AvaloniaList<Avalonia.Controls.Button>
                {
                    importButton,
                    CreateCloseButton(local),
                },
            };
            var options = BuildOptions(local["PeImportDialogTitle"] ?? "", owner);
            var window = SukiMessageBox.CreateMessageBoxWindow(options);
            importVm.Imported += p =>
            {
                onImported?.Invoke(p);
                window.Close();
            };
            WireFootButtons(window, host);
            window.Content = host;
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Error("Open PE import dialog failed", "PromptEngineering", ex);
        }
    }

    /// <summary>构造 PE 服务（DB 持久化；与提示词工程页一致）。</summary>
    private static PmPromptEngineeringService CreatePeService(IServiceProvider service)
    {
        var workspace = service.GetService<IWorkspaceService>();
        return new PmPromptEngineeringService(
            workspaceRoot: workspace?.Root is { } wsRoot && wsRoot.Length > 0 ? wsRoot : null,
            dbFactory: service.GetRequiredService<IDbContextFactory<ComfyDbContext>>());
    }

    /// <summary>宿主 foot 动作按钮（点击执行命令，成功由 Saved/Imported 事件关窗）。</summary>
    private static Avalonia.Controls.Button CreateActionButton(
        SukiMessageBoxResult result, string text, IRelayCommand command)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(result, text);
        button.Click += (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        return button;
    }

    /// <summary>宿主 foot「关闭」按钮（Esc/取消语义）。</summary>
    private static Avalonia.Controls.Button CreateCloseButton(ILocalizer local)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.Close, local["Close"] ?? "");
        button.IsCancel = true;
        return button;
    }

    /// <summary>SukiMessageBox 窗口选项（背景动画等跟随主窗）。</summary>
    private static SukiMessageBoxOptions BuildOptions(string title, Window owner)
    {
        var options = new SukiMessageBoxOptions
        {
            Title = title,
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
        return options;
    }

    /// <summary>宿主 foot 其余按钮（除首个动作按钮外）点击关窗；Esc 关窗。</summary>
    private static void WireFootButtons(Window window, SukiMessageBoxHost host)
    {
        if (host.ActionButtonsSource is { } buttons)
        {
            for (var i = 1; i < buttons.Count; i++)
                buttons[i].Click += (_, _) => window.Close();
            buttons[^1].IsCancel = true;
        }
        window.KeyUp += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) window.Close();
        };
    }
}
