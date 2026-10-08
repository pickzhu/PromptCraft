using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.System;
using Ke.Bee.Localization.Localizer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptCraft;

public partial class ReverseCaptionView : PageBase
{
    /// <summary>素材卡播放按钮：视频 → 卡片内 MediaVideoView 组件 PlayPause（组件与按钮同卡片）。</summary>
    private void OnMediaPlayClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Control c || c.DataContext is not ReverseMediaItem item) return;
            if (!item.IsVideo) return;
            // 从按钮向上逐级找含 MediaVideoView 的 Border（卡片根），组件与按钮是兄弟节点
            var vv = c.GetVisualAncestors()
                .OfType<Border>()
                .Select(b => b.GetVisualDescendants().OfType<PromptCraft.Controls.MediaVideoView>().FirstOrDefault())
                .FirstOrDefault(v => v != null);
            vv?.PlayPause();
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(Localizer.Instance?["MediaPlayFailedLog"] ?? "播放失败: {0}", ex.Message), "ReverseCaption", ex);
        }
    }

    /// <summary>提示词工程下拉：鼠标悬停滚轮不得切换选中值（下拉未展开时拦截滚轮）。</summary>
    private void OnPeProfileComboWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is ComboBox combo && !combo.IsDropDownOpen) e.Handled = true;
    }

    public ReverseCaptionView(IServiceProvider service) : base(service)
    {
        InitializeComponent();
        PeProfileCombo.PointerWheelChanged += OnPeProfileComboWheel;
        // 拖拽上传：路由事件处理器（axaml 上 GlassCard 不支持 AllowDrop/DragDrop 附加属性，故在此注册）
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is ReverseCaptionViewModel vm && vm.IsBusy)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var hasFiles = e.DataTransfer.Items.Any(i => i.Formats.Contains(Avalonia.Input.DataFormat.File));
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ReverseCaptionViewModel vm || vm.IsBusy)
        {
            e.Handled = true;
            return;
        }
        var paths = new List<string>();
        foreach (var item in e.DataTransfer.Items)
        {
            if (!item.Formats.Contains(Avalonia.Input.DataFormat.File))
                continue;
            var raw = item.TryGetRaw(Avalonia.Input.DataFormat.File);
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
        if (paths.Count > 0)
            vm.AddPaths(paths);
        e.Handled = true;
    }
}
