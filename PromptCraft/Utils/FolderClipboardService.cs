using PromptCraft.ViewModels.Folders;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptCraft.Utils;

/// <summary>
/// 跨页共享"文件夹资产剪贴板"（单例，DI 注册）：
/// 菜单管理页资产右键"复制"、提示词库右键"复制"共用同一份中转数据，配合菜单管理"粘贴"把资产加入目标文件夹。
/// 剪贴板仅做 UI 中转（内存态），不持久化；页面切换不丢失。
/// </summary>
public interface IFolderClipboardService
{
    /// <summary>当前剪贴板中的资产（只读快照，调用方不得修改元素）。</summary>
    IReadOnlyList<FolderAssetItem> Items { get; }

    /// <summary>剪贴板是否非空（菜单管理粘贴按钮可用性）。</summary>
    bool HasItems { get; }

    /// <summary>剪贴板内容变化（复制/清空）时触发，宿主页据此刷新绑定。</summary>
    event Action? Changed;

    /// <summary>复制一组资产到剪贴板（覆盖旧内容）。</summary>
    void Copy(IEnumerable<FolderAssetItem> assets);

    /// <summary>清空剪贴板。</summary>
    void Clear();
}

/// <summary>共享剪贴板服务实现（单例，线程亲和 UI：所有调用在 UI 线程）。</summary>
public sealed class FolderClipboardService : IFolderClipboardService
{
    private readonly List<FolderAssetItem> _items = new();

    public IReadOnlyList<FolderAssetItem> Items => _items;

    public bool HasItems => _items.Count > 0;

    public event Action? Changed;

    public void Copy(IEnumerable<FolderAssetItem> assets)
    {
        _items.Clear();
        _items.AddRange(assets.Where(a => a != null));
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }
}
