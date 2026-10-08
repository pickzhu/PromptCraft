using PromptCraft.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BaseClassLib.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Common;
using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using SukiUI.Controls;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 日志查看页：实时展示应用日志，支持按级别/关键字过滤、
/// 错误计数、清空缓冲、打开日志目录、导出错误信息。
/// </summary>
public partial class LogViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private readonly IBaseLogService _log;

    [ObservableProperty] private ListItem<LogLevel> _selectedLevelFilter;
    [ObservableProperty] private string _keyword = string.Empty;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private string _status = string.Empty;

    private readonly AvaloniaList<LogEntry> _all = new();   // 全部（时间倒序，最新在索引 0）
    private readonly AvaloniaList<LogEntry> _view = new();  // 过滤后的展示列表（同样最新在前）
    private const int MaxBuffered = 2000;

    /// <summary>打开日志页时从历史文件统计到的 Error/Fatal 数（叠加到实时计数，不随缓冲裁剪丢失）。</summary>
    private int _baseErrorCount;

    /// <summary>绑定到列表控件的数据源（在代码内维护增删）。</summary>
    public AvaloniaList<LogEntry> Entries => _view;

    public IReadOnlyList<ListItem<LogLevel>> LevelFilterItems { get; }

    public string Title => _local?["LOG"] ?? "";

    public LogViewModel(ILocalizer localizer, IBaseNotice baseNotice, IBaseLogService log)
        : base(localizer, baseNotice)
    {
        _displayName = "LOG";
        _icon = MaterialIconKind.ClipboardText;
        _index = 90;
        _sideMenu = true;
        _log = log;
        LevelFilterItems = new List<ListItem<LogLevel>>
        {
            new(LogLevel.Trace, _local["FilterAll"], 0),
            new(LogLevel.Debug, _local["LogLevelDebug"], 1),
            new(LogLevel.Info, _local["LogLevelInfo"], 2),
            new(LogLevel.Warn, _local["LogLevelWarn"], 3),
            new(LogLevel.Error, _local["LogLevelError"], 4),
            new(LogLevel.Fatal, _local["LogLevelFatal"], 5),
        };
        _selectedLevelFilter = LevelFilterItems[0];
        _errorCount = log.ErrorCount;

        // 载入历史：解析当日日志文件（时间升序 → 反转为倒序）。
        // LoadDailyHistory 已过滤本次进程启动后写入的记录，不会与实时缓冲重复。
        try
        {
            var history = _log.LoadDailyHistory(DateTime.Now);
            // 历史中的错误也要计入错误计数（本次启动的错误由 _log.ErrorCount 实时提供）
            _baseErrorCount = history.Count(e => e.IsError);
            _all.AddRange(history.Reverse());
            TrimAll();
        }
        catch
        {
            // 历史加载失败不影响页面（仅展示本次启动日志）
        }

        // 载入已有日志（RecentEntries 本身为新的在前，直接采用倒序）
        _all.AddRange(log.RecentEntries);
        TrimAll();
        RefreshView();

        // 订阅实时日志（事件可能在线程池线程触发，这里统一回到 UI 线程处理）
        _log.LogWritten += OnLogWritten;
    }

    partial void OnSelectedLevelFilterChanged(ListItem<LogLevel> value) => RefreshView();
    partial void OnKeywordChanged(string value) => RefreshView();
    partial void OnErrorCountChanged(int value) => OnPropertyChanged(nameof(ErrorCountText));

    public string ErrorCountText => string.Format(_local["ErrorsCount"], ErrorCount);

    public override void OnSystemLangueChanged(object? data)
    {
        // 页面标题本地化
        OnPropertyChanged(nameof(Title));
        if (LevelFilterItems is { Count: > 0 })
        {
            LevelFilterItems[0].Text = _local["FilterAll"];
            LevelFilterItems[1].Text = _local["LogLevelDebug"];
            LevelFilterItems[2].Text = _local["LogLevelInfo"];
            LevelFilterItems[3].Text = _local["LogLevelWarn"];
            LevelFilterItems[4].Text = _local["LogLevelError"];
            LevelFilterItems[5].Text = _local["LogLevelFatal"];
        }
        base.OnSystemLangueChanged(data);
    }

    public override void Disposed()
    {
        DisposedFlag = true;
        _log.LogWritten -= OnLogWritten;
        base.Disposed();
    }

    // ---------------------------------------------------------------------
    // 命令
    // ---------------------------------------------------------------------

    [RelayCommand]
    private void ClearLog()
    {
        // 清空内存缓冲 + 删除磁盘上所有历史日志文件（含当日与错误收集文件）
        _log.ClearAll();
        _baseErrorCount = 0;
        _all.Clear();
        _view.Clear();
        ErrorCount = 0;
        Status = _local["LogCleared"];
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var dir = _log.LogDirectory;
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                Status = string.Format(_local["LogDirectoryShown"], dir);
            }
            else
            {
                Status = string.Format(_local["LogDirectoryMissing"], dir);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(_local["OpenLogDirFailed"], ex.Message), "Log");
            Status = string.Format(_local["OpenLogDirFailed"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportErrorsAsync()
    {
        try
        {
            var provider = StorageService.GetStorageProvider();
            if (provider == null)
            {
                Status = _local["NoFileSaveService"];
                return;
            }

            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _local["ExportErrorsTitle"],
                SuggestedFileName = $"promptcraft-errors-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                FileTypeChoices = [new FilePickerFileType(_local["TextFile"]) { Patterns = ["*.txt"] }]
            });
            if (file == null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                Status = _local["CannotParsePath"];
                return;
            }

            await _log.ExportErrorsAsync(path);
            Status = string.Format(_local["ErrorsExported"], _log.ErrorCount, path);
        }
        catch (Exception ex)
        {
            _log.Error(_local["ExportErrorsFailed"], "Log", ex);
            Status = string.Format(_local["ExportFailedDetail"], ex.Message);
        }
    }

    // ---------------------------------------------------------------------
    // 内部逻辑
    // ---------------------------------------------------------------------

    private void OnLogWritten(LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 可能已在本 ViewModel Dispose 后仍触发，安全兜底
            if (DisposedFlag) return;

            // 新日志插入头部，保证最新始终显示在最上面
            _all.Insert(0, entry);
            if (_all.Count > MaxBuffered) _all.RemoveAt(_all.Count - 1);
            ErrorCount = _baseErrorCount + _log.ErrorCount;

            if (MatchesFilter(entry))
            {
                _view.Insert(0, entry);
                if (_view.Count > MaxBuffered) _view.RemoveAt(_view.Count - 1);
            }
        });
    }

    /// <summary>将 _all 裁剪到上限（超出部分移除最旧的，即倒序列表的末尾）。</summary>
    private void TrimAll()
    {
        if (_all.Count > MaxBuffered) _all.RemoveRange(MaxBuffered, _all.Count - MaxBuffered);
    }

    private bool MatchesFilter(LogEntry entry)
    {
        var minLevel = SelectedLevelFilter?.Value ?? LogLevel.Trace;
        if (entry.Level < minLevel) return false;

        var kw = (Keyword ?? string.Empty).Trim();
        if (kw.Length == 0) return true;

        return entry.Message.Contains(kw, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(kw, StringComparison.OrdinalIgnoreCase)
            || entry.LevelText.Contains(kw, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshView()
    {
        var minLevel = SelectedLevelFilter?.Value ?? LogLevel.Trace;
        var kw = (Keyword ?? string.Empty).Trim();
        var hasKw = kw.Length > 0;

        _view.Clear();
        foreach (var entry in _all)
        {
            if (entry.Level < minLevel) continue;
            if (hasKw
                && !entry.Message.Contains(kw, StringComparison.OrdinalIgnoreCase)
                && !entry.Category.Contains(kw, StringComparison.OrdinalIgnoreCase)
                && !entry.LevelText.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _view.Add(entry);
        }
        ErrorCount = _baseErrorCount + _log.ErrorCount;
    }

    private bool DisposedFlag { get; set; }
}
