using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using Ke.Bee.Localization.Localizer;
using Ke.Bee.Localization.Localizer.Abstractions;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>
/// 工作流"配置参数"弹窗：解析 UI 工作流中所有可配置 widget 输入（INT/FLOAT/COMBO/BOOLEAN/STRING），
/// 支持改固定值；种子字段（字段名含 seed）额外支持"随机"，随机范围默认 0~2^32-1（可改）。
/// 保存到 WorkflowParams 表，执行时由 WorkflowJsonConverter.ApplyParams 覆盖后提交。
/// </summary>
public partial class WorkflowParamsModel : ViewModelBase
{
    private readonly int _workflowId;
    private readonly string _workflowJson;
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IBaseLogService _log;

    [ObservableProperty] private ObservableCollection<WorkflowParamItem> _items = new();
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isSaving;

    /// <summary>加载完成且无任何可配置参数时，才显示"未发现可配置参数"（有参数时绝不能显示）。</summary>
    public bool HasNoParams => !IsLoading && Items.Count == 0;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoParams));

    partial void OnItemsChanged(ObservableCollection<WorkflowParamItem> value) => OnPropertyChanged(nameof(HasNoParams));

    /// <summary>保存成功后请求关闭弹窗（由打开方订阅并关闭窗口）。</summary>
    public event Action? RequestClose;

    /// <summary>请求关闭弹窗（View 的"关闭"按钮调用；事件触发权在 VM 内部）。</summary>
    public void RequestCloseNow() => RequestClose?.Invoke();

    public WorkflowParamsModel(
        int workflowId,
        string workflowJson,
        IDbContextFactory<ComfyDbContext> dbFactory,
        ILocalizer localizer,
        IBaseNotice notice,
        IBaseLogService log)
        : base(localizer, notice)
    {
        _workflowId = workflowId;
        _workflowJson = workflowJson;
        _dbFactory = dbFactory;
        _log = log;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var saved = new List<WorkflowParamEntry>();
            if (_workflowId > 0)
            {
                using var db = await _dbFactory.CreateDbContextAsync();
                var row = await db.WorkflowParamsSet.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkflowId == _workflowId);
                if (row != null && !string.IsNullOrEmpty(row.ParamsJson))
                {
                    try
                    {
                        saved = JsonSerializer.Deserialize<List<WorkflowParamEntry>>(row.ParamsJson) ?? new List<WorkflowParamEntry>();
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(string.Format(_local["ParamsParseFailed"], _workflowId), "Workflow", ex);
                    }
                }
            }
            var savedByKey = saved
                .Where(x => !string.IsNullOrEmpty(x.NodeId) && !string.IsNullOrEmpty(x.FieldName))
                .ToDictionary(x => $"{x.NodeId}|{x.FieldName.ToLowerInvariant()}", x => x, StringComparer.Ordinal);

            var list = new List<WorkflowParamItem>();
            using (var doc = JsonDocument.Parse(_workflowJson))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var node in nodes.EnumerateArray())
                    {
                        if (node.ValueKind != JsonValueKind.Object) continue;
                        if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                        if (!node.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
                        var nodeId = idEl.GetInt64().ToString();
                        var nodeType = typeEl.GetString() ?? "";

                        // 新版 ComfyUI 格式：widgets_values_named（{输入名: 值}）为权威真值来源——
                        // 此时节点 inputs 数组只含连线输入（widget 输入不在其中），直接按名生成条目
                        if (node.TryGetProperty("widgets_values_named", out var namedEl)
                            && namedEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var kv in namedEl.EnumerateObject())
                            {
                                if (kv.Name == "control_after_generate") continue; // UI 状态，非参数
                                var key = $"{nodeId}|{kv.Name.ToLowerInvariant()}";
                                savedByKey.TryGetValue(key, out var entry);
                                var defaultValue = FormatValue(kv.Value);
                                list.Add(new WorkflowParamItem
                                {
                                    NodeId = nodeId,
                                    NodeType = nodeType,
                                    FieldName = kv.Name,
                                    FieldType = InferType(kv.Value),
                                    IsSeed = kv.Name.Contains("seed", StringComparison.OrdinalIgnoreCase),
                                    CurrentValue = defaultValue,
                                    NewValue = entry?.UseRandom == true ? (entry.Value ?? defaultValue) : (entry?.Value ?? defaultValue),
                                    UseRandom = entry?.UseRandom ?? false,
                                    RandomMin = entry?.RandomMin ?? 0,
                                    RandomMax = entry?.RandomMax ?? ulong.MaxValue,
                                });
                            }
                            continue;
                        }

                        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array) continue;
                        if (!node.TryGetProperty("widgets_values", out var wvEl) || wvEl.ValueKind != JsonValueKind.Array) continue;
                        var wv = wvEl.EnumerateArray().ToList();

                        // 槽位定位与 ConvertToApi 一致：连线 widget 占槽、control_after_generate 跳过、control 字面量跳过
                        var widgetIdx = 0;
                        foreach (var input in inputs.EnumerateArray())
                        {
                            if (input.ValueKind != JsonValueKind.Object) continue;
                            if (!input.TryGetProperty("name", out var nEl) || nEl.ValueKind != JsonValueKind.String) continue;
                            var name = nEl.GetString()!;
                            var type = input.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                                ? tEl.GetString() : null;
                            var isLink = input.TryGetProperty("link", out var lEl) && lEl.ValueKind == JsonValueKind.Number;
                            var isWidget = IsWidgetType(type);

                            if (isLink) { if (isWidget) widgetIdx++; continue; }
                            if (!isWidget) continue;
                            if (name == "control_after_generate") { widgetIdx++; continue; }
                            if (widgetIdx >= wv.Count) break;

                            var current = wv[widgetIdx];
                            var key = $"{nodeId}|{name.ToLowerInvariant()}";
                            savedByKey.TryGetValue(key, out var entry);

                            var defaultValue = FormatValue(current);
                            list.Add(new WorkflowParamItem
                            {
                                NodeId = nodeId,
                                NodeType = nodeType,
                                FieldName = name,
                                FieldType = type ?? "",
                                IsSeed = name.Contains("seed", StringComparison.OrdinalIgnoreCase),
                                CurrentValue = defaultValue,
                                NewValue = entry?.UseRandom == true ? (entry.Value ?? defaultValue) : (entry?.Value ?? defaultValue),
                                UseRandom = entry?.UseRandom ?? false,
                                RandomMin = entry?.RandomMin ?? 0,
                                RandomMax = entry?.RandomMax ?? ulong.MaxValue,
                            });
                            widgetIdx++;
                            while (widgetIdx < wv.Count
                                && wv[widgetIdx].ValueKind == JsonValueKind.String
                                && IsControlLiteral(wv[widgetIdx].GetString()))
                                widgetIdx++;
                        }
                    }
                }
            }
            Items = new ObservableCollection<WorkflowParamItem>(list);
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["ParamsLoadFailed"], _workflowId), "Workflow", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>保存参数配置到 WorkflowParams 表（每工作流一条，upsert）。</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving) return;
        IsSaving = true;
        try
        {
            var entries = Items
                .Where(x => x.UseRandom || !string.IsNullOrWhiteSpace(x.NewValue))
                .Select(x => new WorkflowParamEntry
                {
                    NodeId = x.NodeId,
                    FieldName = x.FieldName,
                    Value = x.UseRandom ? null : x.NewValue,
                    UseRandom = x.UseRandom,
                    RandomMin = x.RandomMin,
                    RandomMax = x.RandomMax,
                })
                .ToList();
            var json = JsonSerializer.Serialize(entries);
            await new WorkflowParamsRepository(_dbFactory).UpsertAsync(_workflowId, json);
            _log.Info(string.Format(_local["ParamsSaved"], _workflowId, entries.Count), "Workflow");
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["ParamsSaveFailed"], _workflowId), "Workflow", ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static string FormatValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l.ToString() : el.GetDouble().ToString("0.######"),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => el.GetRawText(),
    };

    /// <summary>named 格式无类型信息，按值推断类型（仅用于显示标签；整数→INT、小数→FLOAT、布尔→BOOLEAN、其余→STRING）。</summary>
    private static string InferType(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt64(out _) ? "INT" : "FLOAT",
        JsonValueKind.True or JsonValueKind.False => "BOOLEAN",
        _ => "STRING",
    };

    private static bool IsWidgetType(string? type) => type != null
        && (type.Equals("STRING", StringComparison.OrdinalIgnoreCase)
            || type.Equals("INT", StringComparison.OrdinalIgnoreCase)
            || type.Equals("FLOAT", StringComparison.OrdinalIgnoreCase)
            || type.Equals("COMBO", StringComparison.OrdinalIgnoreCase)
            || type.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)
            || type.Equals("COMFY_DYNAMICCOMBO_V3", StringComparison.OrdinalIgnoreCase)
            || type.Equals("COMFY_DYNAMICCOMBO", StringComparison.OrdinalIgnoreCase)
            || type.Equals("COMFY_STRING", StringComparison.OrdinalIgnoreCase)
            || type.Equals("COMFY_INT", StringComparison.OrdinalIgnoreCase)
            || type.Equals("COMFY_FLOAT", StringComparison.OrdinalIgnoreCase));

    private static bool IsControlLiteral(string? s) => s != null
        && (s.Equals("randomize", StringComparison.OrdinalIgnoreCase)
            || s.Equals("fixed", StringComparison.OrdinalIgnoreCase)
            || s.Equals("increment", StringComparison.OrdinalIgnoreCase)
            || s.Equals("decrement", StringComparison.OrdinalIgnoreCase));
}

/// <summary>参数配置列表项：一个节点的一个 widget 输入。</summary>
public partial class WorkflowParamItem : ObservableObject
{
    public string NodeId { get; init; } = "";
    public string NodeType { get; init; } = "";
    public string FieldName { get; init; } = "";
    public string FieldType { get; init; } = "";
    public bool IsSeed { get; init; }
    public string CurrentValue { get; init; } = "";

    /// <summary>编辑后的固定值。</summary>
    [ObservableProperty] private string _newValue = "";

    /// <summary>种子等数值字段：勾选后执行时随机生成（RandomMin~RandomMax）。</summary>
    [ObservableProperty] private bool _useRandom;

    /// <summary>随机范围下限（默认 0）。</summary>
    [ObservableProperty] private ulong _randomMin;

    /// <summary>随机范围上限（默认 2^64-1 = 18446744073709551615，ComfyUI Seed 全范围）。</summary>
    [ObservableProperty] private ulong _randomMax = ulong.MaxValue;

    public string DisplayName => $"{NodeType} · {FieldName}";

    /// <summary>类型标签：种子字段突出显示。</summary>
    public string TypeTag => IsSeed ? string.Format(Localizer.Instance?["SeedTypeTag"] ?? "", FieldType) : FieldType;

    /// <summary>勾选随机后隐藏固定值输入框、显示随机范围。</summary>
    public bool ShowRandomRange => UseRandom;

    partial void OnUseRandomChanged(bool value) => OnPropertyChanged(nameof(ShowRandomRange));
}
