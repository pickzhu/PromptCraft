using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SukiUI.Content;
using SukiUI.MessageBox;

namespace BaseClassLib.Extends
{
    public static class SukiMessageBoxButtonsFactoryExtend
    {
        public static Button CreateButton(SukiMessageBoxResult result, string? text = null)
        {
            Button button = SukiMessageBoxButtonsFactory.CreateButton(null, result, "Flat");
            PathIcon pathIcon = new PathIcon
            {
                Width = 12.0,
                Height = 12.0,
                Foreground = Brushes.White,
                Margin = new Thickness(0.0, 0.0, 5.0, 0.0)
            };
            switch (result)
            {
                case SukiMessageBoxResult.OK:
                    button.IsDefault = true;
                    pathIcon.Data = Icons.Check;
                    break;
                case SukiMessageBoxResult.Yes:
                    button.IsDefault = true;
                    pathIcon.Data = Icons.Check;
                    break;
                case SukiMessageBoxResult.No:
                    button.IsCancel = true;
                    pathIcon.Data = Icons.Cross;
                    break;
                case SukiMessageBoxResult.Cancel:
                    button.IsCancel = true;
                    pathIcon.Data = Icons.Cancel;
                    break;
                case SukiMessageBoxResult.Apply:
                    button.IsDefault = true;
                    pathIcon.Data = Icons.Check;
                    break;
                case SukiMessageBoxResult.Ignore:
                    pathIcon.Data = Icons.DebugStepOver;
                    break;
                case SukiMessageBoxResult.Retry:
                    button.IsDefault = true;
                    pathIcon.Data = Icons.Refresh;
                    break;
                case SukiMessageBoxResult.Abort:
                    button.IsCancel = true;
                    pathIcon.Data = Icons.Cancel;
                    break;
                case SukiMessageBoxResult.Continue:
                    pathIcon.Data = Icons.ArrowRight;
                    break;
                case SukiMessageBoxResult.Close:
                    button.IsCancel = true;
                    pathIcon.Data = Icons.Logout;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("result", result, null);
            }

            if (text == null)
            {
                text = result.ToString();
            }

            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5.0,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
            {
                (Control)pathIcon,
                (Control)new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = text
                }
            }
            };
            return button;
        }
    }
}
