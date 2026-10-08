using BaseClassLib;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer.Abstractions;
using System;

namespace PromptCraft.BaseModel;

public abstract class ViewModelBase : ModelBase, IDisposable
{
    protected IBaseNotice _noticeService;
    protected ILocalizer _local;
    public ViewModelBase(ILocalizer localizer, IBaseNotice baseNotice)
    {
        _noticeService = baseNotice;
        _local = localizer;
        _noticeService.Subscribe(EventNameConst.SystemLangueChageEvent, OnSystemLangueChanged);
    }

    ~ViewModelBase()
    {
        Dispose();
    }

    public void Dispose()
    {
        _noticeService?.Unsubscribe(EventNameConst.SystemLangueChageEvent, OnSystemLangueChanged);
        Disposed();
        GC.SuppressFinalize(this);
    }

    public virtual void OnSystemLangueChanged(object? data) { }

    /// <summary>
    /// 注销时使用
    /// </summary>
    public virtual void Disposed() { }
}
