using Avalonia.Controls;
using BaseClassLib;
using CommunityToolkit.Mvvm.ComponentModel;
using PromptCraft.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace PromptCraft.Service
{
    public class ViewService : IBaseViewService
    {
        private static readonly Dictionary<Type, Type> _vmToViewMap = [];
        private IServiceProvider? _serviceProvider;
        public IBaseViewService AddView<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(ServiceCollection services, string? serviceKey = null, bool isSignle = true)
            where TView : ContentControl
            where TViewModel : ObservableObject
        {
            var viewType = typeof(TView);
            var viewModelType = typeof(TViewModel);

            _vmToViewMap.Add(viewModelType, viewType);
            Func<Type, Type, IServiceCollection> func = services.AddSingleton;
            Func<Type, IServiceCollection> funcNoParent = services.AddSingleton;
            Func<Type, object, Type, IServiceCollection> keyFunc = services.AddKeyedSingleton;
            if (!isSignle)
            {
                func = services.AddTransient;
                funcNoParent = services.AddTransient;
                keyFunc = services.AddKeyedTransient;
            }
            if (viewModelType.IsAssignableTo(typeof(ModelBase)) && !string.IsNullOrEmpty(serviceKey))
            {
                keyFunc(typeof(ModelBase), serviceKey, viewModelType);
            }
            else if (viewModelType.IsAssignableTo(typeof(ModelBase)))
            {
                func(typeof(ModelBase), viewModelType);
            }
            else if (!string.IsNullOrEmpty(serviceKey))
            {
                if (isSignle)
                {
                    services.AddKeyedSingleton(serviceKey, viewModelType);
                }
                else
                {
                    services.AddKeyedTransient(viewModelType, serviceKey);
                }
            }
            else
            {
                funcNoParent(viewModelType);
            }
            return this;
        }

        public Control CreateView<TViewModel>(IServiceProvider provider, string? key = null) where TViewModel : ObservableObject
        {
            _serviceProvider = provider;
            var viewModelType = typeof(TViewModel);

            if (TryCreateView(provider, viewModelType, out var view, key))
            {
                return view;
            }

            throw new InvalidOperationException();
        }

        public bool TryCreateView(IServiceProvider provider, Type viewModelType, [NotNullWhen(true)] out Control? view, string? key = null)
        {
            _serviceProvider = provider;
            if (!string.IsNullOrEmpty(key))
            {
                var viewModel = provider.GetRequiredKeyedService(viewModelType, key);
                return TryCreateView(viewModel, out view);
            }
            else
            {
                var viewModel = provider.GetRequiredService(viewModelType);
                return TryCreateView(viewModel, out view);
            }

        }

        public bool TryCreateView(object? viewModel, [NotNullWhen(true)] out Control? view, string? key = null)
        {
            view = null;

            if (viewModel == null)
            {
                return false;
            }

            var viewModelType = viewModel.GetType();

            if (_vmToViewMap.TryGetValue(viewModelType, out var viewType))
            {
                var types = GetViewConstructor(viewType);
                object?[] paras = new object[types.Length];
                if (types.Any() && _serviceProvider != null)
                {
                    for (var i = 0; i < types.Length; i++)
                    {
                        paras[i] = _serviceProvider.GetService(types[i]);
                    }
                    view = Activator.CreateInstance(viewType, paras) as Control;
                }
                else
                    view = Activator.CreateInstance(viewType) as Control;

                if (view != null)
                {
                    view.DataContext = viewModel;
                }
            }

            return view != null;
        }

        private Type[] GetViewConstructor(Type type)
        {
            ConstructorInfo constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic).First();
            ParameterInfo[] parameters = constructor.GetParameters();
            return parameters.Select(x => x.ParameterType).ToArray();
        }
    }
}
