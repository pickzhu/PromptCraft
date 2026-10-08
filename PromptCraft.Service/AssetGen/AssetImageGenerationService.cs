using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Service.AssetGen;

// ============================================================
// v2 待办 #4：资产生图「半自动模板制」
// 原则：
//   1) 模板固定「骨架」（标准节点名：CheckpointLoaderSimple / CLIPTextEncode / KSampler / VAEDecode / SaveImage，
//      图生图加 LoadImage / VAEEncode），不内置硬编码模型——模型环境由用户侧 ComfyUI 决定；
//   2) 环境槽（ckpt / sampler / scheduler）提交前从 /object_info 实时解析填充：用户配置的默认模型优先，
//      否则按关键字智能挑选，避免“模型不存在”导致的自动运行报错；
//   3) 提交前 dry-run 校验（fail-fast）：ComfyUI 可达？必需节点存在？COMBO 候选非空？
//      任一不满足即返回可读错误、不提交，提示用户走 4a 手动流程；
//   4) 文生图 / 图生图双骨架并存，参考图自动复制到 ComfyUI input 目录；
//   5) 生成结果落到 pending 目录，由用户验收（确认 → 移入资产目录 / 删除）。
// ============================================================

/// <summary>资产生图请求（一次生成一张）。</summary>
public sealed class AssetGenRequest
{
    /// <summary>正向提示词（缺口提示词正文）。</summary>
    public string PositivePrompt { get; set; } = "";

    /// <summary>负向提示词（可选，默认通用质量负向）。</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>参考图本地路径；提供则走图生图骨架，否则文生图。</summary>
    public string? ReferenceImagePath { get; set; }

    /// <summary>画幅比例：21:9 / 16:9 / 4:3 / 1:1 / 3:4 / 9:16。</summary>
    public string AspectRatio { get; set; } = "16:9";

    /// <summary>资产名（SaveImage 前缀 + 输出命名，缺省用时间戳）。</summary>
    public string? AssetName { get; set; }

    /// <summary>采样步数（默认 25）。</summary>
    public int Steps { get; set; } = 25;

    /// <summary>CFG（默认 6.5）。</summary>
    public double Cfg { get; set; } = 6.5;

    /// <summary>图生图去噪强度（默认 0.75）。</summary>
    public double Denoise { get; set; } = 0.75;
}

/// <summary>环境探测结果（提交前自检依据）。</summary>
public sealed class AssetGenEnvironment
{
    public bool Reachable { get; set; }
    public string? Error { get; set; }
    public List<string> MissingNodes { get; set; } = new();
    public List<string> Checkpoints { get; set; } = new();
    public List<string> Samplers { get; set; } = new();
    public List<string> Schedulers { get; set; } = new();
    public bool InputDirOk { get; set; }
    public string? InputDir { get; set; }

    public bool T2IReady => Reachable && MissingNodes.Count == 0 && Checkpoints.Count > 0;
    public bool I2IReady => T2IReady && InputDirOk;
}

/// <summary>已填充环境的提交计划（Ready=true 才可提交）。</summary>
public sealed class AssetGenPlan
{
    public bool Ready { get; set; }
    public List<string> Errors { get; set; } = new();
    public string PromptJson { get; set; } = "";
    public string CheckpointName { get; set; } = "";
    public string SamplerName { get; set; } = "";
    public string Scheduler { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Prefix { get; set; } = "";
    public bool IsImageToImage { get; set; }
}

/// <summary>单张生成结果（落 pending，待验收）。</summary>
public sealed class GeneratedAsset
{
    public string AssetName { get; set; } = "";
    public string PendingPath { get; set; } = "";
    public string? PromptText { get; set; }
}

public sealed class AssetGenResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<GeneratedAsset> Assets { get; set; } = new();
}

/// <summary>资产生图服务（半自动模板制 + 环境自适应 + fail-fast）。</summary>
public sealed class AssetImageGenerationService
{
    private const string SettingDefaultCkpt = "asset_gen.default_ckpt";

    private readonly IComfyUIClient _client;
    private readonly IAppSettingsRepository _settings;
    private readonly string _comfyOutputDir;
    private readonly string? _comfyInputDir;
    private readonly string _pendingDir;

    // 文生图骨架（API 格式，标准节点名）
    internal static readonly string T2ITemplate = """
        {
          "3": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "{{CKPT}}"}},
          "4": {"class_type": "CLIPTextEncode", "inputs": {"text": "{{POS}}", "clip": ["3", 1]}},
          "5": {"class_type": "CLIPTextEncode", "inputs": {"text": "{{NEG}}", "clip": ["3", 1]}},
          "6": {"class_type": "EmptyLatentImage", "inputs": {"width": {{W}}, "height": {{H}}, "batch_size": 1}},
          "7": {"class_type": "KSampler", "inputs": {"seed": {{SEED}}, "steps": {{STEPS}}, "cfg": {{CFG}}, "sampler_name": "{{SAMPLER}}", "scheduler": "{{SCHEDULER}}", "denoise": 1.0, "model": ["3", 0], "positive": ["4", 0], "negative": ["5", 0], "latent_image": ["6", 0]}},
          "8": {"class_type": "VAEDecode", "inputs": {"samples": ["7", 0], "vae": ["3", 2]}},
          "9": {"class_type": "SaveImage", "inputs": {"filename_prefix": "{{PREFIX}}", "images": ["8", 0]}}
        }
        """;

    // 图生图骨架：LoadImage → VAEEncode → KSampler(denoise<1) → VAEDecode → SaveImage
    internal static readonly string I2ITemplate = """
        {
          "3": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "{{CKPT}}"}},
          "4": {"class_type": "CLIPTextEncode", "inputs": {"text": "{{POS}}", "clip": ["3", 1]}},
          "5": {"class_type": "CLIPTextEncode", "inputs": {"text": "{{NEG}}", "clip": ["3", 1]}},
          "10": {"class_type": "LoadImage", "inputs": {"image": "{{IMAGE}}"}},
          "11": {"class_type": "VAEEncode", "inputs": {"pixels": ["10", 0], "vae": ["3", 2]}},
          "7": {"class_type": "KSampler", "inputs": {"seed": {{SEED}}, "steps": {{STEPS}}, "cfg": {{CFG}}, "sampler_name": "{{SAMPLER}}", "scheduler": "{{SCHEDULER}}", "denoise": {{DENOISE}}, "model": ["3", 0], "positive": ["4", 0], "negative": ["5", 0], "latent_image": ["11", 0]}},
          "8": {"class_type": "VAEDecode", "inputs": {"samples": ["7", 0], "vae": ["3", 2]}},
          "9": {"class_type": "SaveImage", "inputs": {"filename_prefix": "{{PREFIX}}", "images": ["8", 0]}}
        }
        """;

    private static readonly HashSet<string> T2INodes = new(StringComparer.Ordinal)
    { "CheckpointLoaderSimple", "CLIPTextEncode", "KSampler", "VAEDecode", "SaveImage", "EmptyLatentImage" };
    private static readonly HashSet<string> I2INodes = new(StringComparer.Ordinal)
    { "CheckpointLoaderSimple", "CLIPTextEncode", "KSampler", "VAEDecode", "SaveImage", "LoadImage", "VAEEncode" };

    public AssetImageGenerationService(IComfyUIClient client, IAppSettingsRepository settings, ComfySettings comfySettings)
    {
        _client = client;
        _settings = settings;
        _comfyOutputDir = comfySettings.ComfyOutputDir ?? "";
        if (!string.IsNullOrEmpty(_comfyOutputDir))
            _comfyInputDir = Path.Combine(Path.GetDirectoryName(_comfyOutputDir) ?? "", "input");
        _pendingDir = Path.Combine(Path.GetDirectoryName(ComfySettings.DbPath) ?? "", "assetgen_pending");
    }

    // ============================================================
    // 1) 环境探测
    // ============================================================
    public async Task<AssetGenEnvironment> ProbeEnvironmentAsync(CancellationToken ct = default)
    {
        var env = new AssetGenEnvironment();
        ObjectInfoResponse? info;
        try
        {
            info = await _client.GetObjectInfoAsync();
        }
        catch (Exception ex)
        {
            env.Error = $"无法连接 ComfyUI：{ex.Message}";
            return env;
        }
        if (info == null || info.Nodes == null || info.Nodes.Count == 0)
        {
            env.Error = "无法获取 ComfyUI 节点定义（/object_info 为空），请确认 ComfyUI 已启动且未使用受限网络。";
            return env;
        }
        env.Reachable = true;

        foreach (var node in T2INodes)
            if (!info.Nodes.ContainsKey(node)) env.MissingNodes.Add(node);

        // 采样器/调度器候选（ComfyUI 内核自带，但部分魔改版可能改名）
        if (info.Nodes.TryGetValue("KSampler", out var ks))
        {
            env.Samplers = TryGetCombo(ks, "sampler_name");
            env.Schedulers = TryGetCombo(ks, "scheduler");
        }
        if (info.Nodes.TryGetValue("CheckpointLoaderSimple", out var ck))
            env.Checkpoints = TryGetCombo(ck, "ckpt_name");

        env.InputDirOk = !string.IsNullOrEmpty(_comfyInputDir) && Directory.Exists(_comfyInputDir);
        env.InputDir = _comfyInputDir;
        return env;
    }

    // ============================================================
    // 2) 提交前计划：选骨架 + 环境槽填充 + fail-fast 校验
    // ============================================================
    public async Task<AssetGenPlan> PreparePlanAsync(AssetGenRequest request, CancellationToken ct = default)
    {
        var plan = new AssetGenPlan();
        if (string.IsNullOrWhiteSpace(request.PositivePrompt))
        {
            plan.Errors.Add("正向提示词不能为空。");
            return plan;
        }

        var isI2I = !string.IsNullOrWhiteSpace(request.ReferenceImagePath) && File.Exists(request.ReferenceImagePath);
        plan.IsImageToImage = isI2I;

        // 环境探测（fail-fast：探测失败/不满足即不提交）
        var env = await ProbeEnvironmentAsync(ct);
        if (!env.Reachable)
        {
            plan.Errors.Add(env.Error ?? "ComfyUI 不可达。");
            return plan;
        }
        if (env.MissingNodes.Count > 0)
        {
            plan.Errors.Add("当前 ComfyUI 缺少标准节点：" + string.Join("、", env.MissingNodes)
                + "。模板无法兼容，请改用「手动生成」流程（v1.5 资产生图）。");
            return plan;
        }
        if (isI2I)
        {
            if (!env.InputDirOk)
                plan.Errors.Add("未找到 ComfyUI 的 input 目录（"
                    + (_comfyInputDir ?? "未知") + "）。图生图需要把参考图复制进 ComfyUI，请先在 ComfyUI 设置中指定输出目录，或改用文生图。");
        }
        if (env.Checkpoints.Count == 0)
        {
            plan.Errors.Add("ComfyUI 没有任何可用的 checkpoint 模型（请先在 ComfyUI 下载模型）。");
        }
        else
        {
            var preferred = (await _settings.GetAsync(SettingDefaultCkpt, ct)) ?? "";
            plan.CheckpointName = PickCheckpoint(env.Checkpoints, preferred);
            plan.SamplerName = PickSampler(env.Samplers);
            plan.Scheduler = PickScheduler(env.Schedulers);
        }
        if (plan.Errors.Count > 0) return plan;

        // 尺寸（按画幅 + 模型档位自适应，避免 SD1.5 用 SDXL 尺寸）
        var (w, h) = ResolveSize(request.AspectRatio, plan.CheckpointName);
        plan.Width = w;
        plan.Height = h;
        plan.Prefix = "promptcraft_asset_" + SanitizeName(string.IsNullOrWhiteSpace(request.AssetName) ? "asset" : request.AssetName!);

        // 参考图 → ComfyUI input 目录
        string? imageName = null;
        if (isI2I)
        {
            try
            {
                imageName = SanitizeName(Path.GetFileName(request.ReferenceImagePath!));
                File.Copy(request.ReferenceImagePath!, Path.Combine(_comfyInputDir!, imageName), true);
            }
            catch (Exception ex)
            {
                plan.Errors.Add($"参考图复制到 ComfyUI input 目录失败：{ex.Message}");
                return plan;
            }
        }

        // 填充模板
        var template = isI2I ? I2ITemplate : T2ITemplate;
        var negative = string.IsNullOrWhiteSpace(request.NegativePrompt)
            ? "lowres, bad anatomy, bad hands, text, error, missing fingers, extra digit, fewer digits, cropped, worst quality, low quality, jpeg artifacts, signature, watermark, username, blurry, ugly"
            : request.NegativePrompt!;
        var json = template
            .Replace("{{CKPT}}", JsonEscape(plan.CheckpointName))
            .Replace("{{POS}}", JsonEscape(request.PositivePrompt))
            .Replace("{{NEG}}", JsonEscape(negative))
            .Replace("{{SAMPLER}}", JsonEscape(plan.SamplerName))
            .Replace("{{SCHEDULER}}", JsonEscape(plan.Scheduler))
            .Replace("{{PREFIX}}", JsonEscape(plan.Prefix))
            .Replace("{{W}}", plan.Width.ToString())
            .Replace("{{H}}", plan.Height.ToString())
            .Replace("{{SEED}}", Random.Shared.Next(1, int.MaxValue).ToString())
            .Replace("{{STEPS}}", Math.Clamp(request.Steps, 1, 100).ToString())
            .Replace("{{CFG}}", request.Cfg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        if (isI2I)
        {
            json = json.Replace("{{DENOISE}}", request.Denoise.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                       .Replace("{{IMAGE}}", JsonEscape(imageName!));
        }

        // 提交前最终校验：JSON 合法 + 骨架完整（dry-run）
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var required = isI2I ? I2INodes : T2INodes;
            foreach (var node in required)
            {
                var found = root.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("class_type", out var ctProp) && ctProp.GetString() == node);
                if (!found)
                {
                    plan.Errors.Add($"模板填充后缺失节点 {node}（dry-run 校验未通过）。");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            plan.Errors.Add($"模板填充后 JSON 校验失败：{ex.Message}");
            return plan;
        }
        if (plan.Errors.Count > 0) return plan;

        plan.PromptJson = json;
        plan.Ready = true;
        return plan;
    }

    // ============================================================
    // 3) 提交 + 跟踪 + 落 pending
    // ============================================================
    public async Task<AssetGenResult> GenerateAsync(AssetGenPlan plan, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var result = new AssetGenResult();
        if (!plan.Ready)
        {
            result.Error = plan.Errors.Count > 0 ? string.Join(" ", plan.Errors) : "计划未就绪（请先 PreparePlanAsync 并检查 Ready）。";
            return result;
        }

        Directory.CreateDirectory(_pendingDir);
        var clientId = Guid.NewGuid().ToString("N");
        onProgress?.Invoke("正在提交到 ComfyUI…");
        var submit = await _client.SubmitWorkflowAsync(plan.PromptJson, clientId);
        if (!submit.Success || string.IsNullOrEmpty(submit.PromptId))
        {
            result.Error = submit.ErrorMessage ?? "提交失败（ComfyUI 拒绝执行）。";
            return result;
        }

        // 轮询历史直至完成（最长 ~5 分钟）
        onProgress?.Invoke("已提交，等待 ComfyUI 生成…");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        HistoryDetailResponse? history = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);
            history = await _client.GetHistoryAsync(submit.PromptId);
            if (history != null)
            {
                if (!string.IsNullOrEmpty(history.StatusMessage))
                {
                    result.Error = $"ComfyUI 执行出错：{history.StatusMessage}";
                    return result;
                }
                if (history.Completed) break;
            }
        }
        if (history == null || !history.Completed)
        {
            result.Error = "等待 ComfyUI 生成超时（5 分钟）。请检查 ComfyUI 队列是否卡住，可稍后在 ComfyUI 面板查看。";
            return result;
        }

        var outputs = history.OutputImages.Where(i => string.Equals(i.Type ?? "output", "output", StringComparison.OrdinalIgnoreCase)).ToList();
        if (outputs.Count == 0)
        {
            result.Error = "ComfyUI 已执行完成，但未返回任何输出图片（SaveImage 可能被改名，模板不兼容）。";
            return result;
        }

        if (string.IsNullOrEmpty(_comfyOutputDir) || !Directory.Exists(_comfyOutputDir))
        {
            result.Error = "未配置 ComfyUI 输出目录（ComfyOutputDir），无法读取生成结果。";
            return result;
        }

        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        for (var i = 0; i < outputs.Count; i++)
        {
            var src = Path.Combine(_comfyOutputDir, outputs[i].FileName);
            if (!File.Exists(src)) continue;
            var name = string.IsNullOrWhiteSpace(plan.Prefix) ? "asset" : plan.Prefix.Replace("promptcraft_asset_", "");
            var pending = Path.Combine(_pendingDir, $"{name}_{ts}{(outputs.Count > 1 ? $"_{i + 1}" : "")}.png");
            File.Copy(src, pending, true);
            result.Assets.Add(new GeneratedAsset
            {
                AssetName = SanitizeName(name),
                PendingPath = pending,
                PromptText = plan.IsImageToImage ? null : "文生图",
            });
        }
        result.Success = result.Assets.Count > 0;
        if (!result.Success)
            result.Error = "ComfyUI 返回了输出记录，但输出目录中未找到对应图片文件。";
        onProgress?.Invoke(result.Success ? $"生成完成，共 {result.Assets.Count} 张，等待验收。" : result.Error);
        return result;
    }

    // ============================================================
    // 4) 验收：确认 → 移入资产目录；删除 → 丢弃
    // ============================================================
    public async Task<string?> AcceptAssetAsync(string pendingPath, string assetName, string assetDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pendingPath) || !File.Exists(pendingPath)) return null;
        if (string.IsNullOrWhiteSpace(assetDirectory)) return null;
        Directory.CreateDirectory(assetDirectory);
        var baseName = SanitizeName(string.IsNullOrWhiteSpace(assetName) ? "asset" : assetName);
        var final = Path.Combine(assetDirectory, baseName + ".png");
        var idx = 1;
        while (File.Exists(final))
        {
            final = Path.Combine(assetDirectory, $"{baseName}_{idx}.png");
            idx++;
        }
        File.Copy(pendingPath, final, false);
        TryDelete(pendingPath);
        LogService.Instance.Info($"资产生图验收通过：{baseName} → {final}", "AssetGen");
        return final;
    }

    public void RejectAsset(string pendingPath)
    {
        if (string.IsNullOrWhiteSpace(pendingPath)) return;
        TryDelete(pendingPath);
        LogService.Instance.Info($"资产生图验收拒绝，已丢弃：{pendingPath}", "AssetGen");
    }

    /// <summary>清理所有未验收的 pending 资产。</summary>
    public void ClearPending()
    {
        try
        {
            if (Directory.Exists(_pendingDir))
                foreach (var f in Directory.EnumerateFiles(_pendingDir))
                    TryDelete(f);
        }
        catch { }
    }

    public string PendingDir => _pendingDir;

    // ============================================================
    // 内部工具
    // ============================================================
    private static string PickCheckpoint(List<string> list, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && list.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            return list.First(c => c.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        foreach (var kw in new[] { "flux", "sdxl", "xl", "sd15", "1.5", "sd3", "playground", "pixel" })
        {
            var hit = list.FirstOrDefault(c => c.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return list.FirstOrDefault() ?? "";
    }

    private static string PickSampler(List<string> list)
    {
        if (list.Count == 0) return "euler";
        foreach (var kw in new[] { "euler", "euler_ancestral", "dpmpp_2m" })
        {
            var hit = list.FirstOrDefault(s => s.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return list[0];
    }

    private static string PickScheduler(List<string> list)
    {
        if (list.Count == 0) return "normal";
        foreach (var kw in new[] { "normal", "karras", "simple", "sgm_uniform" })
        {
            var hit = list.FirstOrDefault(s => s.Equals(kw, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return list[0];
    }

    private static (int w, int h) ResolveSize(string ratio, string checkpoint)
    {
        var small = checkpoint.Contains("sd15", StringComparison.OrdinalIgnoreCase)
            || checkpoint.Contains("1.5", StringComparison.OrdinalIgnoreCase)
            || checkpoint.Contains("sd1", StringComparison.OrdinalIgnoreCase);
        return ratio switch
        {
            "21:9" => small ? (896, 384) : (1344, 640),
            "16:9" => small ? (768, 512) : (1216, 832),
            "4:3" => small ? (704, 576) : (1152, 896),
            "1:1" => small ? (640, 640) : (1024, 1024),
            "3:4" => small ? (576, 704) : (896, 1152),
            "9:16" => small ? (512, 768) : (832, 1216),
            _ => small ? (640, 640) : (1024, 1024),
        };
    }

    /// <summary>从节点定义提取 COMBO 候选（object_info 字段 = [type, [candidates...]]）。</summary>
    private static List<string> TryGetCombo(NodeObjectInfo node, string fieldName)
    {
        if (node.Input.Required.TryGetValue(fieldName, out var req) && req.Options is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in je.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
                else if (item.ValueKind == JsonValueKind.Array)
                    foreach (var sub in item.EnumerateArray())
                        if (sub.ValueKind == JsonValueKind.String) list.Add(sub.GetString() ?? "");
            }
            return list;
        }
        return new List<string>();
    }

    private static string JsonEscape(string s) => JsonSerializer.Serialize(s); // 序列化为带引号的 JSON 字符串字面量

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Where(c => !invalid.Contains(c)).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(s) ? "asset" : s;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
