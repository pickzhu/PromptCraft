using System;
using Avalonia;
using Avalonia.Controls;

namespace PromptCraft.Controls
{
    public class WaterfallPanel : Panel
    {
        // 属性：基础最小列宽（不再是死宽度，而是作为列宽的“最小值/基准值”）
        public static readonly StyledProperty<double> ColumnWidthProperty =
            AvaloniaProperty.Register<WaterfallPanel, double>(nameof(ColumnWidth), defaultValue: 200.0);

        public double ColumnWidth
        {
            get => GetValue(ColumnWidthProperty);
            set => SetValue(ColumnWidthProperty, value);
        }

        // 属性：网格间距
        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<WaterfallPanel, double>(nameof(Spacing), defaultValue: 10.0);

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        // 1. 测量阶段
        protected override Size MeasureOverride(Size availableSize)
        {
            double baseColWidth = ColumnWidth;
            double spacing = Spacing;

            // 如果可用宽度是无限大（例如被包裹在没有限制宽度的容器中），采用基准列宽
            double availableWidth = double.IsPositiveInfinity(availableSize.Width) ? 800 : availableSize.Width;

            // 计算当前总宽度能容纳多少列（至少 1 列）
            int columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (baseColWidth + spacing)));

            // 【核心改动】：将右侧剩余的空白空间，平均分配给现有的每一列
            double totalSpacingWidth = (columns - 1) * spacing;
            double actualColWidth = (availableWidth - totalSpacingWidth) / columns;

            // 数组记录每一列的累积高度
            double[] columnHeights = new double[columns];

            foreach (var child in Children)
            {
                if (child == null || !child.IsVisible) continue;

                // 使用重新计算后的【动态列宽】去测量子元素
                child.Measure(new Size(actualColWidth, double.PositiveInfinity));

                // 寻找当前最矮的那一列
                int minCol = 0;
                for (int i = 1; i < columns; i++)
                {
                    if (columnHeights[i] < columnHeights[minCol])
                    {
                        minCol = i;
                    }
                }

                // 累加该子元素的高度和间距
                columnHeights[minCol] += child.DesiredSize.Height + spacing;
            }

            // 找出所有列中最长的一列，作为面板的最终期望高度
            double maxHeight = 0;
            for (int i = 0; i < columns; i++)
            {
                if (columnHeights[i] > maxHeight)
                {
                    maxHeight = columnHeights[i];
                }
            }

            if (maxHeight > 0) maxHeight -= spacing;

            // 宽度直接返回填满视口的可用宽度，彻底消除最右侧的空白
            return new Size(availableWidth, maxHeight);
        }

        // 2. 排布阶段
        protected override Size ArrangeOverride(Size finalSize)
        {
            double baseColWidth = ColumnWidth;
            double spacing = Spacing;
            double finalWidth = finalSize.Width;

            // 同样重新计算在最终排布尺寸下的【动态列宽】
            int columns = Math.Max(1, (int)Math.Floor((finalWidth + spacing) / (baseColWidth + spacing)));

            double totalSpacingWidth = (columns - 1) * spacing;
            double actualColWidth = (finalWidth - totalSpacingWidth) / columns;

            double[] columnHeights = new double[columns];

            foreach (var child in Children)
            {
                if (child == null || !child.IsVisible) continue;

                // 寻找当前最矮的那一列
                int minCol = 0;
                for (int i = 1; i < columns; i++)
                {
                    if (columnHeights[i] < columnHeights[minCol])
                    {
                        minCol = i;
                    }
                }

                // 基于【动态列宽】计算当前子元素的具体 X 和 Y 坐标
                double x = minCol * (actualColWidth + spacing);
                double y = columnHeights[minCol];

                // 安排子元素位置，传入的是动态放大后的实际列宽
                child.Arrange(new Rect(x, y, actualColWidth, child.DesiredSize.Height));

                // 更新当前列的高度
                columnHeights[minCol] += child.DesiredSize.Height + spacing;
            }

            return finalSize;
        }
    }
}
