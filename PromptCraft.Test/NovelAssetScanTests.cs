using System.Reflection;
using PromptCraft.Models.Inference.Novel;
using PromptCraft.Service.Inference.Novel;

namespace PromptCraft.Test;

// 资产扫描（S5）人物资产规则回归测试：
// ① CleanupAssetName 弱清洗——「未命名」字样剥离、角色情节状态后缀（C01-哭泣）剥离，合法服饰/发型变体（C01-A）不受影响；
// ② DedupAssets 变体合并——无段号基础资产（C01）与段号变体（C01-A）不得互相吞并，保证服饰/发型变体不丢失；
// ③ FilterGapAssets 缺口过滤——情节状态资产与衣物单品不生成提示词，只保留角色三视图[基础+不同衣服变体]/场景/道具；
// ④ BuildSubsequentRefs 参考图——仅当资产目录真实存在该角色资产图时才给后续变体加参考图，否则一律不带。
[TestClass]
public sealed class NovelAssetScanTests
{
    private static object? Invoke(string methodName, params object[] args)
    {
        var m = typeof(NovelToPromptService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(m, $"私有方法 {methodName} 不存在");
        return m!.Invoke(null, args);
    }

    private static string Cleanup(string input) => (string)Invoke("CleanupAssetName", input)!;

    private static List<string> Dedup(params string[] items) => (List<string>)Invoke("DedupAssets", items.ToList())!;

    [TestMethod]
    public void Cleanup_StripsUnnamedPlaceholder()
    {
        Assert.AreEqual("C03_少年", Cleanup("C03_未命名的少年"));
        Assert.AreEqual("女人", Cleanup("未命名的女人"));
        Assert.AreEqual("主角的母亲（林母）", Cleanup("主角的母亲（林母）"));
        Assert.AreEqual("女主的妹妹", Cleanup("女主的妹妹"));
    }

    [TestMethod]
    public void Cleanup_ReturnsEmptyWhenOnlyUnnamed()
    {
        Assert.AreEqual("", Cleanup("未命名"));
        Assert.AreEqual("", Cleanup("未命名的角色"));
    }

    [TestMethod]
    public void Cleanup_StripsPlotStateSuffix()
    {
        Assert.AreEqual("C01", Cleanup("C01-哭泣"));
        Assert.AreEqual("C01", Cleanup("C01_微笑"));
        Assert.AreEqual("C02", Cleanup("C02-受伤"));
    }

    [TestMethod]
    public void Cleanup_KeepsLegalCostumeVariantsAndSceneProps()
    {
        Assert.AreEqual("C01-A_常服", Cleanup("C01-A_常服"));
        Assert.AreEqual("C01-B_晚礼服", Cleanup("C01-B_晚礼服"));
        Assert.AreEqual("scene_S01_医馆", Cleanup("scene_S01_医馆"));
        Assert.AreEqual("prop_药箱", Cleanup("prop_药箱"));
    }

    [TestMethod]
    public void Dedup_KeepsBaseAndSegmentVariants()
    {
        var r = Dedup("C01", "C01-A_常服", "C01-B_晚礼服");
        CollectionAssert.AreEquivalent(new[] { "C01", "C01-A_常服", "C01-B_晚礼服" }, r);
    }

    [TestMethod]
    public void Dedup_MergesSameSegmentNameWithDescription()
    {
        var r = Dedup("C01-A", "C01-A_墨绿旗袍");
        CollectionAssert.AreEquivalent(new[] { "C01-A" }, r);
    }

    [TestMethod]
    public void Dedup_AfterCleanup_KeepsBaseAndVariant()
    {
        var r = Dedup(Cleanup("C01-哭泣"), "C01-A_常服");
        CollectionAssert.AreEquivalent(new[] { "C01", "C01-A_常服" }, r);
    }

    [TestMethod]
    public void FilterGapAssets_DropsStateAndClothing_KeepsBaseVariantSceneProp()
    {
        var input = new List<string>
        {
            "C01", "C01-A_常服", "C01-哭泣", "C01_风衣", "scene_S01_医馆", "prop_药箱", "C03_主角的母亲（林母）",
        };
        var r = ((IReadOnlyList<string>)Invoke("FilterGapAssets", input)!);
        CollectionAssert.AreEquivalent(
            new[] { "C01", "C01-A_常服", "scene_S01_医馆", "prop_药箱", "C03_主角的母亲（林母）" }, r.ToList());
    }

    [TestMethod]
    public void FilterGapAssets_EmptyReturnsEmpty()
    {
        var r = ((IReadOnlyList<string>)Invoke("FilterGapAssets", new List<string>())!);
        Assert.AreEqual(0, r.Count);
    }

    private static NovelPipelineContext NewAssetCtx(string assetDir) => new()
    {
        Options = new NovelPromptOptions { AssetDirectory = assetDir, OutputLanguage = NovelOutputLanguage.Chinese },
    };

    [TestMethod]
    public void SubsequentRefs_OnlyWhenRealAssetExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "promptcraft_asset_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "C01.png"), "x");
            var r = (List<(string Asset, string BaseId, string Primary)>)Invoke("BuildSubsequentRefs",
                NewAssetCtx(dir), new List<string> { "C01-A", "C01-B" }, new List<string> { "C01-B" })!;
            Assert.AreEqual(1, r.Count, "有已存在 C01 资产图时，后续变体应带参考图");
            Assert.AreEqual("C01-B", r[0].Item1);
            Assert.AreEqual("C01", r[0].Item2);
            Assert.AreEqual("C01", r[0].Item3, "参考图来源应为已存在的 C01 资产图");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SubsequentRefs_NoReferenceWhenNoExistingAsset()
    {
        var dir = Path.Combine(Path.GetTempPath(), "promptcraft_asset_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 目录为空 → 该角色无真实资产图 → 一律不带参考图（第一个提示词绝不含参考图）
            var r = (List<(string Asset, string BaseId, string Primary)>)Invoke("BuildSubsequentRefs",
                NewAssetCtx(dir), new List<string> { "C01-A", "C01-B" }, new List<string> { "C01-B" })!;
            Assert.AreEqual(0, r.Count, "无已存在资产图时不得要求参考图");
        }
        finally { Directory.Delete(dir, true); }
    }
}
