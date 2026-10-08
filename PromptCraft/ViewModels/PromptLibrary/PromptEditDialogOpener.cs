using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using Ke.Bee.Localization.Localizer;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.PromptLibrary;

/// <summary>
/// 词库编辑对话框的统一打开入口（词库页面 / 图库详情页共用）。
/// 宿主为 SukiMessageBox 弹窗（与 ImageDetail 一致），保存成功后触发 Saved 并关闭。
/// </summary>
public static class PromptEditDialogOpener
{
    /// <param name="existing">null = 新建</param>
    /// <param name="dialogTitle">弹窗标题</param>
    /// <param name="beforeOpen">打开前对 VM 的预填（如图库提取的正负提示词/参数）</param>
    /// <param name="onSaved">保存成功回调（宿主刷新数据）</param>
    public static async Task OpenEditAsync(
        IServiceProvider service,
        Prompt? existing,
        string dialogTitle,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer local,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log,
        Action<PromptEditModel>? beforeOpen = null,
        Action? onSaved = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            var library = service.GetRequiredService<IPromptLibraryService>();
            var folderService = service.GetRequiredService<IFolderService>();
            var workspace = service.GetRequiredService<IWorkspaceService>();
            var viewService = service.GetRequiredService<IBaseViewService>();

            var editVm = new PromptEditModel(existing, library, folderService, workspace, service, local, notice, log);
            beforeOpen?.Invoke(editVm);

            if (!viewService.TryCreateView(editVm, out var editView, "PromptEdit")) return;

            var host = new SukiMessageBoxHost
            {
                Content = editView,
                IconPreset = null,
                Width = 820,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
                // 宿主 foot：[保存（关闭按钮左侧，点击执行 SaveCommand，成功 Saved → 关窗）] [关闭]
                ActionButtonsSource = new AvaloniaList<Avalonia.Controls.Button>
                {
                    CreateSaveButton(editVm),
                    CreateCloseButton(),
                },
            };
            var options = new SukiMessageBoxOptions
            {
                Title = dialogTitle,
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
            editVm.Saved += () =>
            {
                onSaved?.Invoke();
                window.Close();
            };
            window.Content = host;
            // 宿主 foot 按钮：[保存] 点击执行 SaveCommand（成功 → Saved → 关窗）；[关闭] 关窗
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
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            log.Error(Localizer.Instance?["PromptEditOpenFailed"] ?? "", "PromptLibrary", ex);
        }
    }

    /// <summary>宿主 foot"保存"按钮（位于关闭按钮左侧；点击执行 SaveCommand，成功由 Saved 事件关窗）。</summary>
    private static Avalonia.Controls.Button CreateSaveButton(PromptEditModel vm)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.OK, Localizer.Instance?["Save"] ?? "");
        button.Click += (_, _) => vm.SaveCommand.Execute(null);
        return button;
    }

    /// <summary>宿主 foot"关闭"按钮（Esc/取消语义）。</summary>
    private static Avalonia.Controls.Button CreateCloseButton()
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.Close, Localizer.Instance?["Close"] ?? "");
        button.IsCancel = true;
        return button;
    }

    /// <summary>扩写结果 → 保存到提示词库（对齐 PromptMaster PromptEditForm 预填：标题=创作需求前 40 字，note=扩写自：xxx）。</summary>
    public static async Task OpenForExpandAsync(
        IServiceProvider service,
        string expandedPrompt,
        string shortText,
        Action? onSaved = null)
    {
        var local = service.GetRequiredService<Ke.Bee.Localization.Localizer.Abstractions.ILocalizer>();
        var notice = service.GetRequiredService<PromptCraft.Interfaces.IBaseNotice>();
        var log = service.GetRequiredService<PromptCraft.Interfaces.IBaseLogService>();

        var demand = (shortText ?? "").Trim();
        var title = demand.Length > 0 ? demand[..Math.Min(demand.Length, 40)] : (local["ExpandFallbackTitle"] ?? "");

        await OpenEditAsync(
            service,
            existing: null,
            dialogTitle: local["ExpandSaveToLibrary"] ?? "",
            local,
            notice,
            log,
            beforeOpen: vm =>
            {
                vm.Title = title;
                vm.Positive = expandedPrompt ?? "";
                vm.Negative = "";
                vm.Note = string.Format(local["ExpandFromFormat"] ?? "扩写自：{0}", demand);
            },
            onSaved: onSaved);
    }
}
