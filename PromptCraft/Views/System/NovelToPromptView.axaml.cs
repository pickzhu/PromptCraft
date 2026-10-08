using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.System;

namespace PromptCraft;

public partial class NovelToPromptView : PageBase
{
    public NovelToPromptView(IServiceProvider service) : base(service)
    {
        InitializeComponent();
    }

    /// <summary>双击阶段列表项：已确认阶段回显历史结果，再次双击退出。</summary>
    private void OnStageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: NovelToPromptViewModel.StageItem item }
            && DataContext is NovelToPromptViewModel vm)
        {
            vm.ShowStageHistoryCommand.Execute(item);
            e.Handled = true;
        }
    }
}

/// <summary>int 与指定参数相等的 bool 转换（RadioButton 按 int 索引选中显示），双向。</summary>
public class IntEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        if (value is string s && int.TryParse(s, out var i)) return i == ToInt(parameter);
        if (value is int ii) return ii == ToInt(parameter);
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ToInt(parameter) : BindingOperations.DoNothing;

    private static int ToInt(object? parameter)
        => parameter is string ps && int.TryParse(ps, out var p) ? p
           : parameter is int pi ? pi : 0;
}
