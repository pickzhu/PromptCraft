using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PromptCraft.ViewModels.PromptLibrary;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PromptCraft.Views.PromptLibrary;

/// <summary>
/// 封面大图预览：显示大图（封面拖拽导出已按用户要求移除，关闭走宿主 foot）。
/// </summary>
public partial class PromptBigImageView : UserControl
{
    public PromptBigImageView()
    {
        InitializeComponent();
    }

    private PromptCardItem? Card => DataContext as PromptCardItem;
}
