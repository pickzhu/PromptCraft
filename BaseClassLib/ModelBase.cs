using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace BaseClassLib
{
    public abstract partial class ModelBase : ObservableValidator
    {
        public ModelBase() { }

        [ObservableProperty]
        protected string _displayName = string.Empty;
        [ObservableProperty]
        protected MaterialIconKind _icon = MaterialIconKind.Icc;
        [ObservableProperty]
        protected string _description = string.Empty;
        [ObservableProperty]
        protected int _index = 0;
        [ObservableProperty]
        protected bool _sideMenu = false;
    }
}
