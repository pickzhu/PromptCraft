using Avalonia.Controls;
using BaseClassLib.Extends;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer.Abstractions;
using System;

namespace PromptCraft.BaseModel
{
    public class PageBase : UserControl, IDisposable
    {
        protected readonly IServiceProvider _service;
        protected readonly IBaseNotice _notice;
        protected readonly ILocalizer _local;
        public PageBase(IServiceProvider service)
        {
            _service = service;
            _notice = service.GetService<IBaseNotice>(typeof(IBaseNotice))!;
            _local = service.GetService<ILocalizer>(typeof(ILocalizer))!;
        }

        protected virtual void OnSystemLangueChanged(object? obj) { }

        public void Dispose()
        {
            _notice.Unsubscribe(EventNameConst.SystemLangueChageEvent, OnSystemLangueChanged);
            Disposed();
            GC.SuppressFinalize(this);
        }

        public virtual void Disposed() { }
    }
}
