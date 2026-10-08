using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PromptCraft.ViewModels.System;
using System.Collections.Generic;
using System.Linq;

namespace PromptCraft;

/// <summary>提示词工程「JSON 导入」对话框内容（导入/关闭按钮在宿主 foot，与词库添加提示词弹窗同构）。支持拖拽 JSON 到内容区。</summary>
public partial class PeImportView : UserControl
{
    public PeImportView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private PeImportModel? Vm => DataContext as PeImportModel;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Vm == null)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var hasFiles = e.DataTransfer.Items.Any(i => i.Formats.Contains(DataFormat.File));
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null)
        {
            e.Handled = true;
            return;
        }
        var paths = new List<string>();
        foreach (var item in e.DataTransfer.Items)
        {
            if (!item.Formats.Contains(DataFormat.File))
                continue;
            var raw = item.TryGetRaw(DataFormat.File);
            switch (raw)
            {
                case IStorageItem storage:
                {
                    var p = storage.TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(p))
                        paths.Add(p);
                    break;
                }
                case string s when !string.IsNullOrWhiteSpace(s):
                    paths.Add(s);
                    break;
            }
        }
        // 多文件拖入只取第一个（对齐 PromptMaster「已选择第一个 JSON 文件」）
        if (paths.Count > 0)
            Vm.SetImportFile(paths[0]);
        e.Handled = true;
    }
}
