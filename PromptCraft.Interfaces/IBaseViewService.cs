using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace PromptCraft.Interfaces
{
    public interface IBaseViewService
    {
        public IBaseViewService AddView<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(ServiceCollection services, string? serviceKey = null, bool isSignle = true)
        where TView : ContentControl
        where TViewModel : ObservableObject;

        public bool TryCreateView(IServiceProvider provider, Type viewModelType, [NotNullWhen(true)] out Control? view, string? key = null);

        public bool TryCreateView(object? viewModel, [NotNullWhen(true)] out Control? view, string? key = null);

        public Control CreateView<TViewModel>(IServiceProvider provider, string? key = null) where TViewModel : ObservableObject;
    }
}
