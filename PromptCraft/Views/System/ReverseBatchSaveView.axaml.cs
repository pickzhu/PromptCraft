using Avalonia.Controls;
using Avalonia.Input;
using PromptCraft.ViewModels.System;

namespace PromptCraft;

/// <summary>批量保存到词库对话框（对齐 PromptMaster 批量保存）。</summary>
public partial class ReverseBatchSaveView : UserControl
{
    public ReverseBatchSaveView()
    {
        InitializeComponent();
    }

    /// <summary>点击封面 → 选择图片（对齐 PromptEditDialogView.OnCoverClick）。</summary>
    private void OnCoverClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ReverseBatchSaveModel vm && vm.PickCoverCommand.CanExecute(null))
            vm.PickCoverCommand.Execute(null);
    }
}
