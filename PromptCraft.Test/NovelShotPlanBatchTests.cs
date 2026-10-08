using System.Reflection;
using PromptCraft.Models.Inference.Novel;
using PromptCraft.Service.Inference.Novel;

namespace PromptCraft.Test;

// 镜头规划分批「越生成越多」（40→75）修复验证：
// ①镜头总数预估锚点（估算内满批继续防漏，超出后最终确认轮防膨胀）；
// ②推进协议只认单独一行的 CONTINUE/DONE（防对白/动作中 continue 字样误触发）；
// ③跨批内容级去重（模型批次漂移重排前文时剔除重复镜头）。
[TestClass]
public sealed class NovelShotPlanBatchTests
{
    private static object? Invoke(string methodName, params object[] args)
    {
        var m = typeof(NovelToPromptService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(m, $"私有方法 {methodName} 不存在");
        return m!.Invoke(null, args);
    }

    private static ShotItem NewShot(string id, string firstFrame, string action, string dialogue, string purpose = "建立情绪")
    {
        var shot = new ShotItem
        {
            ShotId = id,
            SourceScene = "场1",
            Duration = 6,
            Purpose = purpose,
            ShotSize = "中景",
            CameraPosition = "正面",
            FocalLength = "35mm",
            CameraMovement = "缓推",
            WorldPosition = "雨夜街头",
            ScreenPosition = "画面中心",
            Gaze = "望向对方",
            AxisSide = "A 侧",
            Action = action,
            Performance = "平静",
            PropsState = "无",
            Dialogue = dialogue,
            DialogueCharCount = dialogue.Length,
            DialogueSeconds = dialogue.Length * 0.25,
            AssetsUsed = "短发女人",
            FirstFrame = firstFrame,
            LastFrame = firstFrame + "（尾帧）",
            ContinuityRisk = "无",
        };
        return shot;
    }

    [TestMethod]
    public void ContinueFlagOnlyMatchesDedicatedLine()
    {
        // 单独一行 CONTINUE → 命中（旧逻辑 Contains 会误判的情况）
        Assert.IsTrue((bool)Invoke("HasLineFlag", "[\"a\"]\n\nCONTINUE\n", "CONTINUE")!, "单独一行 CONTINUE 应命中");
        Assert.IsTrue((bool)Invoke("HasLineFlag", "continue", "CONTINUE")!, "忽略大小写应命中");

        // 对白/动作中的 continue 字样不得误触发
        Assert.IsFalse((bool)Invoke("HasLineFlag", "She continues walking.", "CONTINUE")!, "句子中的 continue 不应命中");
        Assert.IsFalse((bool)Invoke("HasLineFlag", "{\"Action\":\"continue forward\"}", "CONTINUE")!, "JSON 值中的 continue 不应命中");
        Assert.IsFalse((bool)Invoke("HasLineFlag", "[\"a\"]", "CONTINUE")!, "无标记不应命中");
        // 独立成行（允许缩进/前后空白）应命中
        Assert.IsTrue((bool)Invoke("HasLineFlag", "[\"a\"]\n  CONTINUE  \n", "CONTINUE")!, "独立行带缩进应命中");
        Assert.IsTrue((bool)Invoke("HasLineFlag", "CONTINUE\nwrapped-in-body", "CONTINUE")!, "CONTINUE 独占首行应命中");
    }

    [TestMethod]
    public void ShotCountEstimateParsing()
    {
        Assert.AreEqual(12, (int?)Invoke("TryParseShotCount", "12")!, "纯整数应解析");
        Assert.AreEqual(12, (int?)Invoke("TryParseShotCount", "约 12 个镜头")!, "中文包裹应取首个整数");
        Assert.AreEqual(8, (int?)Invoke("TryParseShotCount", "8")!, "8 应解析");
        Assert.IsNull(Invoke("TryParseShotCount", "无法估算"), "无数字应返回 null");
        Assert.IsNull(Invoke("TryParseShotCount", ""), "空串应返回 null");
        Assert.AreEqual(999, (int?)Invoke("TryParseShotCount", "1500")!, "超过上限应 clamp 到 999");
    }

    [TestMethod]
    public void NormalBatchesFromEstimate()
    {
        Assert.AreEqual(1, (int)Invoke("EstimateNormalBatches", 1)!, "1 镜 → 1 批");
        Assert.AreEqual(2, (int)Invoke("EstimateNormalBatches", 8)!, "8 镜 → ceil(8/5)=2 批");
        Assert.AreEqual(3, (int)Invoke("EstimateNormalBatches", 11)!, "11 镜 → 3 批");
        Assert.AreEqual(30, (int)Invoke("EstimateNormalBatches", (int?)null)!, "预估失败 → 回退全局上限");
    }

    [TestMethod]
    public void DuplicateShotsDroppedAcrossBatches()
    {
        var existing = new List<ShotItem>
        {
            NewShot("SH001", "雨夜街头，短发女人站在路灯下。", "她缓缓抬起手，望向远方。", "你说的是真的吗？"),
        };

        // 完全相同（仅 ID/尾帧措辞微差）→ 判重复
        var dup = NewShot("SH999", "雨夜街头，短发女人站在路灯下。", "她缓缓抬起手，望向远方。", "你说的是真的吗？");
        dup.LastFrame = "灯下女人收手。";
        Assert.IsTrue((bool)Invoke("IsDuplicateShot", existing, dup)!, "内容一致的镜头应判重复");

        // 措辞微变但实质相同 → 判重复
        var nearDup = NewShot("SH999", "雨夜街头，短发女人站路灯下", "缓缓抬起手望向远方", "你说的都是真的吗？");
        Assert.IsTrue((bool)Invoke("IsDuplicateShot", existing, nearDup)!, "高度相似的镜头应判重复");

        // 实质不同的镜头（动作/对白均不同）→ 不判重复
        var diff = NewShot("SH999", "男人推门走入咖啡店。", "他在柜台前停下，环顾四周。", "来一杯美式。");
        Assert.IsFalse((bool)Invoke("IsDuplicateShot", existing, diff)!, "内容不同的镜头不应判重复");
    }

    [TestMethod]
    public void FinalCheckInstructionAnchorsToEstimate()
    {
        var existing = Enumerable.Range(1, 8)
            .Select(i => NewShot($"SH{i:000}", $"帧 {i}", $"动作 {i}", ""))
            .ToList();
        var instruction = NovelPromptEngineering.BuildShotPlanFinalCheckInstruction(9, existing, 8);
        Assert.IsTrue(instruction.Contains("预估本场约 8 个镜头"), "确认轮应引用预估总数");
        Assert.IsTrue(instruction.Contains("目前已生成 8 个"), "确认轮应回传已生成数量");
        Assert.IsTrue(instruction.Contains("SH009"), "确认轮应指向下一全局编号");
        Assert.IsTrue(instruction.Contains("输出空数组 [] 并单独一行输出 DONE"), "确认轮应允许空数组收尾");
    }
}
