using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptCraft.Service.AssetGen;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PromptCraft.Test;

/// <summary>
/// 资产生图模板骨架测试（v2 #4）：
/// 文生图/图生图双骨架填充后 JSON 合法、标准节点齐全、占位符完整可替换（替换后无残留）；
/// PreparePlanAsync 在正向提示词为空时 fail-fast 返回可读错误。
/// </summary>
[TestClass]
public class AssetGenTemplateTests
{
    private static string FillTemplate(string template)
        => template
            .Replace("{{CKPT}}", "test.safetensors")
            .Replace("{{POS}}", "prompt")
            .Replace("{{NEG}}", "negative")
            .Replace("{{SAMPLER}}", "euler")
            .Replace("{{SCHEDULER}}", "normal")
            .Replace("{{PREFIX}}", "prefix")
            .Replace("{{W}}", "1024")
            .Replace("{{H}}", "1024")
            .Replace("{{SEED}}", "1")
            .Replace("{{STEPS}}", "25")
            .Replace("{{CFG}}", "6.5")
            .Replace("{{DENOISE}}", "0.75")
            .Replace("{{IMAGE}}", "ref.png");

    /// <summary>模板含裸数字占位符（{{W}} 等），未填充时不是合法 JSON；提交前 dry-run 校验的就是填充后 JSON。</summary>
    [DataTestMethod]
    [DataRow(nameof(AssetImageGenerationService.T2ITemplate))]
    [DataRow(nameof(AssetImageGenerationService.I2ITemplate))]
    public void FilledTemplate_IsValidJson_WithRequiredNodes(string templateName)
    {
        var template = templateName == nameof(AssetImageGenerationService.T2ITemplate)
            ? AssetImageGenerationService.T2ITemplate
            : AssetImageGenerationService.I2ITemplate;
        var filled = FillTemplate(template);

        using var doc = JsonDocument.Parse(filled);
        var required = templateName == nameof(AssetImageGenerationService.T2ITemplate)
            ? new[] { "CheckpointLoaderSimple", "CLIPTextEncode", "KSampler", "VAEDecode", "SaveImage", "EmptyLatentImage" }
            : new[] { "CheckpointLoaderSimple", "CLIPTextEncode", "KSampler", "VAEDecode", "SaveImage", "LoadImage", "VAEEncode" };

        var present = new List<string>();
        foreach (var node in doc.RootElement.EnumerateObject())
        {
            if (node.Value.TryGetProperty("class_type", out var ct))
                present.Add(ct.GetString() ?? "");
        }
        foreach (var node in required)
            Assert.IsTrue(present.Contains(node), $"模板 {templateName} 缺少必需节点 {node}");
    }

    [DataTestMethod]
    [DataRow(nameof(AssetImageGenerationService.T2ITemplate))]
    [DataRow(nameof(AssetImageGenerationService.I2ITemplate))]
    public void Template_FillsAllPlaceholders(string templateName)
    {
        var template = templateName == nameof(AssetImageGenerationService.T2ITemplate)
            ? AssetImageGenerationService.T2ITemplate
            : AssetImageGenerationService.I2ITemplate;
        var filled = FillTemplate(template);
        Assert.IsFalse(filled.Contains("{{"), $"模板 {templateName} 填充后有残留占位符");
        using var doc = JsonDocument.Parse(filled); // 填充后仍须为合法 JSON
    }

    [TestMethod]
    public async Task PreparePlan_EmptyPrompt_FailsFast()
    {
        var service = new AssetImageGenerationService(
            new NoOpComfyClient(), new NoOpSettings(), new PromptCraft.Models.ComfyUI.ComfySettings());
        var plan = await service.PreparePlanAsync(new AssetGenRequest { PositivePrompt = "" });
        Assert.IsFalse(plan.Ready);
        Assert.IsTrue(plan.Errors.Count > 0);
        Assert.IsTrue(plan.Errors.Any(e => e.Contains("正向提示词")));
    }

    private sealed class NoOpComfyClient : PromptCraft.Interfaces.IComfyUIClient
    {
        public Task<bool> TestConnectionAsync() => Task.FromResult(false);
        public Task<PromptCraft.Models.ComfyUI.QueueStatusResponse?> GetQueueStatusAsync() => Task.FromResult<PromptCraft.Models.ComfyUI.QueueStatusResponse?>(null);
        public Task<PromptCraft.Models.ComfyUI.ObjectInfoResponse?> GetObjectInfoAsync() => Task.FromResult<PromptCraft.Models.ComfyUI.ObjectInfoResponse?>(null);
        public Task<List<PromptCraft.Models.ComfyUI.UserDataFileInfo>> ListUserDataWorkflowsAsync() => Task.FromResult(new List<PromptCraft.Models.ComfyUI.UserDataFileInfo>());
        public Task<string?> GetUserDataFileAsync(string file) => Task.FromResult<string?>(null);
        public Task<bool> SaveUserDataFileAsync(string file, string content) => Task.FromResult(false);
        public Task<bool> DeleteUserDataFileAsync(string file) => Task.FromResult(false);
        public Task<PromptCraft.Models.ComfyUI.SubmitWorkflowResponse> SubmitWorkflowAsync(string promptJson, string clientId, string? uiWorkflowJson = null)
            => Task.FromResult(new PromptCraft.Models.ComfyUI.SubmitWorkflowResponse { Success = false, ErrorMessage = "noop" });
        public Task<PromptCraft.Models.ComfyUI.HistoryDetailResponse?> GetHistoryAsync(string promptId) => Task.FromResult<PromptCraft.Models.ComfyUI.HistoryDetailResponse?>(null);
    }

    private sealed class NoOpSettings : PromptCraft.Interfaces.IAppSettingsRepository
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(new Dictionary<string, string>());
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetManyAsync(IEnumerable<KeyValuePair<string, string>> items, CancellationToken ct = default) => Task.CompletedTask;
    }
}
