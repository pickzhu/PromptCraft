using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer;
using System.Collections.Generic;

namespace BaseClassLib
{
    public class ViewLocator(IBaseViewService viewService) : IDataTemplate
    {
        private readonly Dictionary<object, Control> _controlCache = [];
        public Control? Build(object? param)
        {
            if (param is null)
            {
                return CreateText(Localizer.Instance?["DataNull"] ?? "");
            }

            if (_controlCache.TryGetValue(param, out var control))
            {
                return control;
            }

            if (viewService.TryCreateView(param, out var view))
            {
                _controlCache.Add(param, view);

                return view;
            }

            return CreateText($"No View For {param.GetType().Name}.");
        }

        public bool Match(object? data) => data is ObservableObject;
        private static TextBlock CreateText(string text) => new TextBlock { Text = text };
    }
}
