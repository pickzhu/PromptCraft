using CommunityToolkit.Mvvm.ComponentModel;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer.Abstractions;
using SukiUI.Controls;
using System;
using System.Collections.ObjectModel;

namespace PromptCraft.ViewModels.Test
{
    // 将 CardItem 放在当前 ViewModel 命名空间下，方便前台解析
    public class CardItem
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double RandomHeight { get; set; } // 用于模拟不规则内容的块高
    }

    public partial class TestViewModel : ViewModelBase, ISukiStackPageTitleProvider
    {
        [ObservableProperty]
        private ObservableCollection<CardItem> _items;

        public TestViewModel(ILocalizer localizer, IBaseNotice baseNotice) : base(localizer, baseNotice)
        {
            _index = 500;
            _sideMenu = true;
            _displayName = "Test";

            Items = new ObservableCollection<CardItem>();
            var rand = new Random();

            // 生成 20 条模拟数据，包含不同长度的文本和高度
            for (int i = 1; i <= 20; i++)
            {
                Items.Add(new CardItem
                {
                    Title = $"卡片标题 #{i}",
                    Description = i % 3 == 0
                        ? "这是一段比较短的描述。"
                        : "这是一段非常长长长长长长长长长长长长长长长长长长长长长长长长长长长的描述，用来撑开卡片高度，形成瀑布流交错的效果。",
                    RandomHeight = rand.Next(80, 200) // 动态高度
                });
            }
        }

        public string Title => "Test";
    }
}
