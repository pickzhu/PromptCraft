using PromptCraft.Service.Inference;
using PromptCraft.Service.Inference.Minimax;

namespace PromptCraft.Test;

// 迁移验证：MiniMax 场景目录 / showIf 表达式 / normalizeForm / resolveMinimaxScenarioExpand（catalog.js + assemble.js 对齐）
[TestClass]
public sealed class ExpandMiniMaxTests
{
    private static readonly string[] AllPeIds =
    {
        "pe_expand_h3_full_reference",
        "pe_expand_minimax_continuous_story",
        "pe_expand_minimax_product_ad",
        "pe_expand_minimax_handdrawn",
        "pe_expand_minimax_coop_game",
        "pe_expand_minimax_paper_collage",
        "pe_expand_minimax_brand_promo",
        "pe_expand_minimax_mv",
        "pe_expand_minimax_papercraft",
        "pe_expand_minimax_anim3d",
    };

    [TestMethod]
    public void AllTenScenarioPeIdsResolve()
    {
        foreach (var peId in AllPeIds)
        {
            var sc = MiniMaxScenarios.GetScenarioByPeId(peId);
            Assert.IsNotNull(sc, $"peId 未命中: {peId}");
            Assert.IsTrue(sc!.FormFields.Count > 0, $"场景 {peId} 无表单字段");
        }
        Assert.AreEqual(10, MiniMaxScenarios.All.Count);
    }

    [TestMethod]
    public void BuiltinProfilesPeIdsMatchCatalog()
    {
        foreach (var peId in AllPeIds)
        {
            var profile = ExpandPromptEngineering.FindProfile(peId);
            Assert.IsNotNull(profile, $"BuiltinProfiles 缺少: {peId}");
            Assert.AreEqual(ExpandPromptEngineering.ExpandProfileKind.Minimax, profile!.Kind);
        }
    }

    [TestMethod]
    public void ShowIfEqualsAndNotEquals()
    {
        // plan_mode == custom（catalog matchShowIf equals）
        var eq = new MiniMaxShowIf { Key = "plan_mode", Equals = "custom" };
        Assert.IsTrue(eq.Match(new Dictionary<string, string> { ["plan_mode"] = "custom" }));
        Assert.IsFalse(eq.Match(new Dictionary<string, string> { ["plan_mode"] = "ai" }));
        Assert.IsFalse(eq.Match(new Dictionary<string, string>())); // 缺省 = 空串 ≠ custom

        // dialogue_mode != none（catalog matchShowIf notEquals）
        var ne = new MiniMaxShowIf { Key = "dialogue_mode", NotEquals = "none" };
        Assert.IsTrue(ne.Match(new Dictionary<string, string> { ["dialogue_mode"] = "dialogue" }));
        Assert.IsFalse(ne.Match(new Dictionary<string, string> { ["dialogue_mode"] = "none" }));
    }

    [TestMethod]
    public void ShowIfGteLteAndAll()
    {
        // segment_i_beat（i≥2）: all{plan_mode=custom, segment_count gte i}
        var expr = new MiniMaxShowIf
        {
            All = new List<MiniMaxShowIf>
            {
                new() { Key = "plan_mode", Equals = "custom" },
                new() { Key = "segment_count", Gte = "5" },
            },
        };
        Assert.IsTrue(expr.Match(new Dictionary<string, string>
        {
            ["plan_mode"] = "custom", ["segment_count"] = "6",
        }));
        Assert.IsFalse(expr.Match(new Dictionary<string, string>
        {
            ["plan_mode"] = "custom", ["segment_count"] = "4",
        }));
        Assert.IsFalse(expr.Match(new Dictionary<string, string>
        {
            ["plan_mode"] = "ai", ["segment_count"] = "6",
        }));

        // Any：dialogue_mode==none 命中任一分支即 true
        var any = new MiniMaxShowIf
        {
            Any = new List<MiniMaxShowIf>
            {
                new() { Key = "dialogue_mode", Equals = "none" },
                new() { Key = "plan_mode", Equals = "custom" },
            },
        };
        Assert.IsTrue(any.Match(new Dictionary<string, string> { ["plan_mode"] = "custom" }));
        Assert.IsTrue(any.Match(new Dictionary<string, string> { ["dialogue_mode"] = "none" }));
        Assert.IsFalse(any.Match(new Dictionary<string, string> { ["dialogue_mode"] = "voice" }));
    }

    [TestMethod]
    public void NormalizeFormFillsDefaults()
    {
        var sc = MiniMaxScenarios.GetScenarioByPeId("pe_expand_h3_full_reference")!;
        var form = MiniMaxAssembler.NormalizeForm(sc, new Dictionary<string, string>());
        Assert.AreEqual("10", form["duration_seconds"]);   // catalog 默认 '10'
        Assert.AreEqual("16:9", form["aspect_ratio"]);     // catalog 默认 '16:9'
        Assert.AreEqual("strict", form["expand_mode"]);    // catalog 默认 'strict'
    }

    [TestMethod]
    public void ResolveMinimaxScenarioExpandFullReference()
    {
        var profile = ExpandPromptEngineering.FindProfile("pe_expand_h3_full_reference")!;
        var result = MiniMaxAssembler.ResolveMinimaxScenarioExpand(profile,
            new ExpandPromptEngineering.ExpandResolveParams(
                ShortText: "一只戴红围巾的猫在雪地里走",
                OutputLang: "zh",
                ExpandLen: "long",
                ExpandLenChars: null,
                UserExtraPrompt: "补充：背景要有雪山",
                MinimaxForm: new Dictionary<string, string>()));
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.System.Contains("MiniMax-H3"));
        Assert.IsTrue(result.System.Contains("【User additional requirements】补充：背景要有雪山"));
        Assert.IsTrue(result.User.Contains("一只戴红围巾的猫在雪地里走"));
        Assert.IsTrue(result.MaxTokens >= MiniMaxAssembler.MediaExpandMin);
    }

    [TestMethod]
    public void DirectorSegmentsContractInUser()
    {
        // continuous_story + plan_mode=custom：user 应带「第二部分」段提示词结构
        var profile = ExpandPromptEngineering.FindProfile("pe_expand_minimax_continuous_story")!;
        var form = new Dictionary<string, string>
        {
            ["prompt_kind"] = "t2v",
            ["plan_mode"] = "custom",
            ["segment_count"] = "4",
            ["segment_seconds"] = "5",
            ["aspect_ratio"] = "16:9",
            ["visual_style"] = "赛博朋克",
            ["segment_1_beat"] = "开场镜头",
            ["segment_2_beat"] = "推进镜头",
            ["segment_3_beat"] = "冲突爆发",
            ["segment_4_beat"] = "雨夜对峙",
        };
        var result = MiniMaxAssembler.ResolveMinimaxScenarioExpand(profile,
            new ExpandPromptEngineering.ExpandResolveParams(
                ShortText: "两个角色在雨夜城市相遇",
                OutputLang: "zh",
                ExpandLen: "long",
                ExpandLenChars: null,
                UserExtraPrompt: "",
                MinimaxForm: form));
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.User.Contains("第 4 段在干什么"), "表单参数应包含第 4 段在干什么行（formToLines）");
        Assert.IsTrue(result.User.Contains("===== 提示词组"));
    }

    [TestMethod]
    public void MvSubtitlePeIdIsMinimaxMv()
    {
        var profile = ExpandPromptEngineering.FindProfile("pe_expand_minimax_mv");
        Assert.IsNotNull(profile);
        var sc = MiniMaxScenarios.GetScenarioByPeId("pe_expand_minimax_mv");
        Assert.IsNotNull(sc);
        Assert.AreEqual("pe_expand_minimax_mv", sc!.PeId);
    }
}
