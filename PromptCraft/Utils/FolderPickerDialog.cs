using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using PromptCraft.ViewModels.Folders;
using Ke.Bee.Localization.Localizer;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PromptCraft.Utils;

/// <summary>
/// 加入文件夹弹窗打开器（提示词/工作流/图库三页共用）：
/// 多选文件夹 → foot [取消][保存]；返回所选文件夹 Id 集合，取消/Esc 返回 null，保存但未选任何文件夹返回空列表。
/// </summary>
public static class FolderPickerDialog
{
    public static async Task<List<int>?> PickAsync(
        string scope,
        IReadOnlyList<int>? preSelected,
        IServiceProvider service)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return null;
        }

        var vm = new FolderPickerModel(service, scope, preSelected);
        var view = new Views.Folders.FolderPickerView { DataContext = vm };

        var host = new SukiMessageBoxHost
        {
            Content = view,
            IconPreset = null,
            Width = 380,
            ActionButtonsPreset = SukiMessageBoxButtons.Close,
            // 宿主 foot：[取消] [保存]
            ActionButtonsSource = new Avalonia.Collections.AvaloniaList<Avalonia.Controls.Button>
            {
                CreateCancelButton(),
                CreateSaveButton(vm),
            },
        };

        var options = new SukiMessageBoxOptions
        {
            Title = Localizer.Instance?["AddToFolder"] ?? "加入文件夹",
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
        List<int>? result = null;
        vm.Confirmed += ids => { result = ids; window.Close(); };
        vm.Cancelled += () => window.Close();

        window.Content = host;
        if (host.ActionButtonsSource is { } buttons)
        {
            buttons[0].Click += (_, _) => vm.CancelCommand.Execute(null);
            buttons[1].Click += (_, _) => vm.ConfirmCommand.Execute(null);
            buttons[0].IsCancel = true;
        }
        window.KeyUp += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        await window.ShowDialog(owner);
        return result;
    }

    private static Avalonia.Controls.Button CreateCancelButton()
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(
            SukiMessageBoxResult.Cancel, Localizer.Instance?["PeCancelButton"] ?? "");
        button.IsCancel = true;
        return button;
    }

    private static Avalonia.Controls.Button CreateSaveButton(FolderPickerModel? vm)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(
            SukiMessageBoxResult.OK, Localizer.Instance?["PeSaveButton"] ?? "");
        if (vm != null) button.Click += (_, _) => vm.ConfirmCommand.Execute(null);
        return button;
    }
}
