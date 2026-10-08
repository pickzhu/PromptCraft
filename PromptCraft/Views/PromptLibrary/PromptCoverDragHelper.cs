using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using System;
using System.Threading.Tasks;

namespace PromptCraft.Views.PromptLibrary;

/// <summary>
/// 封面拖拽导出帮助（对齐 PromptMaster pm-native-drag-file：按住封面拖到 QQ/ComfyUI 等应用）。
/// Avalonia 12：PointerPressed 时构造 DataTransfer（文件项）并调用 DragDrop.DoDragDropAsync；
/// 无实际拖动时返回 None，调用方据此决定是否继续执行单击（编辑）。
/// </summary>
public static class PromptCoverDragHelper
{
    /// <summary>
    /// 尝试拖拽导出指定文件。
    /// 仅左键按下才启动拖拽：右键（菜单）进入 DoDragDropAsync 会开启拖拽模态循环，
    /// 使随后打开的右键 Popup 定位错乱（显示到视口左上角）。右键一律返回 false，
    /// 事件继续冒泡到卡片右键菜单（与图库图片卡一致：无拖拽处理器，菜单定位正常）。
    /// </summary>
    /// <returns>true = 拖拽已导出（拖动发生）；false = 无拖动或无法拖出（可继续单击行为）。</returns>
    public static async Task<bool> TryExportAsync(Control host, PointerPressedEventArgs e, string? filePath)
    {
        // 只有左键拖拽才进入 OLE 拖拽循环；右键/中键直接放行（供卡片右键菜单正常定位）
        if (!e.GetCurrentPoint(host).Properties.IsLeftButtonPressed) return false;
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return false;

        try
        {
            var top = TopLevel.GetTopLevel(host);
            var file = top?.StorageProvider is { } sp
                ? await StorageProviderExtensions.TryGetFileFromPathAsync(sp, filePath)
                : null;
            if (file == null) return false;

            var dt = new DataTransfer();
            dt.Add(DataTransferItem.CreateFile(file));
            var result = await DragDrop.DoDragDropAsync(e, dt, DragDropEffects.Copy);
            return result != DragDropEffects.None;
        }
        catch (Exception)
        {
            return false; // 拖拽失败静默，回退单击行为
        }
    }
}
