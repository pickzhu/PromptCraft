using System.Reflection;
using PromptCraft.Models.Inference.Novel;
using PromptCraft.Service.Inference.Novel;

namespace PromptCraft.Test;

// 小说页 S7 迭代：①无参考图降级三字段 T2VA 开关（默认关闭，仅六段式生效）；②六段式/导演台注入资产目录真实资产图作 <Picture N>；
// ③导演台段时长参考化。验证开关分支、字段标题（随输出语言）、指南资源加载、资产图解析与 r2v 注入。
[TestClass]
public sealed class NovelT2VAFallbackTests
{
    private static NovelPipelineContext NewCtx(
        NovelOutputFormat format,
        NovelOutputLanguage lang = NovelOutputLanguage.Chinese,
        bool enableNoRefT2VA = false,
        string? assetDir = null) => new()
    {
        Characters = "短发女人：三十岁，黑色短发，穿深灰风衣。",
        ConfirmedAssets = { "短发女人（角色）" },
        Options = new NovelPromptOptions
        {
            OutputFormat = format,
            OutputLanguage = lang,
            AspectRatio = "16:9",
            DefaultShotSeconds = 6,
            EnableNoRefT2VA = enableNoRefT2VA,
            AssetDirectory = assetDir,
        },
    };

    private static ShotItem NewShot(string id, string scene, double dur, string action = "行走", string dialogue = "") => new()
    {
        ShotId = id,
        SourceScene = scene,
        Duration = dur,
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
        FirstFrame = "雨夜街头，短发女人站在路灯下。",
        LastFrame = "短发女人转身。",
        ContinuityRisk = "无",
    };

    private static object? Invoke(string methodName, params object[] args)
    {
        var m = typeof(NovelToPromptService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(m, $"私有方法 {methodName} 不存在");
        return m!.Invoke(null, args);
    }

    private static string NewTempAssetDir(params string[] fileNames)
    {
        var dir = Path.Combine(Path.GetTempPath(), "promptcraft-t2va-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in fileNames) File.WriteAllText(Path.Combine(dir, f), "placeholder");
        return dir;
    }

    [TestMethod]
    public void T2vaSwitchDefaultsToFalse()
    {
        Assert.IsFalse(new NovelPromptOptions().EnableNoRefT2VA, "无参考图降级 T2VA 开关默认必须关闭");
    }

    [TestMethod]
    public void T2vaFieldTitlesFollowOutputLanguage()
    {
        var zh = H3T2VAPromptBuilder.FieldTitles(NovelOutputLanguage.Chinese);
        CollectionAssert.AreEqual(new[] { "综合多模态描述:", "整体声景:", "非叙事配乐:" }, zh, "中文应输出中文三字段标题");

        var en = H3T2VAPromptBuilder.FieldTitles(NovelOutputLanguage.English);
        CollectionAssert.AreEqual(new[] { "integrated_multimodal_description:", "overall_soundscape:", "non_diegetic_music:" }, en, "English 应输出官方英文字段名");
    }

    [TestMethod]
    public void T2vaGuidesEmbeddedAndLoadable()
    {
        var zh = H3T2VAPromptBuilder.LoadGuide("zh");
        var en = H3T2VAPromptBuilder.LoadGuide("en");
        Assert.IsFalse(string.IsNullOrWhiteSpace(zh), "中文 T2VA 指南应可加载");
        Assert.IsFalse(string.IsNullOrWhiteSpace(en), "英文 T2VA 指南应可加载");
        Assert.IsTrue(zh.Contains("综合多模态描述"), "中文指南应含三字段结构");
        Assert.IsTrue(en.Contains("integrated_multimodal_description"), "英文指南应含官方字段名");
    }

    [TestMethod]
    public void SixSectionUsesAssemblerWhenSwitchOff()
    {
        // 开关关闭：即使无资产图，六段式仍走 MiniMaxAssembler（有参考图版本契约）
        var ctx = NewCtx(NovelOutputFormat.H3FullReference, enableNoRefT2VA: false);
        var system = (string)Invoke("BuildShotSystem", ctx, NewShot("1-1", "场1", 6), false, null)!;
        Assert.IsTrue(system.Contains("主体定义"), "开关关闭应走六段式 system");
        Assert.IsFalse(system.Contains("综合多模态描述:"), "开关关闭不应出现 T2VA 三字段标题");
    }

    [TestMethod]
    public void SixSectionFallsBackToT2vaWhenSwitchOnAndNoRef()
    {
        // 开关开启 + 本镜头无资产图：六段式降级为三字段 T2VA（中文字段标题）
        var ctx = NewCtx(NovelOutputFormat.H3FullReference, enableNoRefT2VA: true);
        var system = (string)Invoke("BuildShotSystem", ctx, NewShot("1-1", "场1", 6), false, null)!;
        Assert.IsTrue(system.Contains("综合多模态描述:"), "开关开启且无参考图应降级 T2VA system");
        Assert.IsTrue(system.Contains("整体声景:"), "T2VA system 应含第二字段");
        Assert.IsTrue(system.Contains("非叙事配乐:"), "T2VA system 应含第三字段");
        Assert.IsFalse(system.Contains("主体定义:"), "T2VA 模式不应出现六段式标题（带冒号）");
    }

    [TestMethod]
    public void SixSectionKeepsAssemblerWhenSwitchOnWithRef()
    {
        // 开关开启 + 本镜头有资产图：仍走六段式（有参考图版本，锁角色）
        var dir = NewTempAssetDir("短发女人.png");
        try
        {
            var shot = NewShot("1-1", "场1", 6);
            var media = (List<string>)Invoke("ResolveShotRefImages", NewCtx(NovelOutputFormat.H3FullReference, enableNoRefT2VA: true, assetDir: dir), shot)!;
            Assert.AreEqual(1, media.Count, "资产目录应解析出 1 张参考图");
            var system = (string)Invoke("BuildShotSystem", NewCtx(NovelOutputFormat.H3FullReference, enableNoRefT2VA: true, assetDir: dir), shot, false, media)!;
            Assert.IsTrue(system.Contains("主体定义"), "有参考图时不得降级，仍走六段式");
            Assert.IsTrue(system.Contains("<Picture 1>"), "六段式 system 应注入 <Picture N> 参考标签");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void BuildShotUserFallsBackToT2vaWhenSwitchOnAndNoRef()
    {
        var ctx = NewCtx(NovelOutputFormat.H3FullReference, enableNoRefT2VA: true);
        var user = (string)Invoke("BuildShotUser", ctx, NewShot("1-1", "场1", 6), (ShotItem?)null, 0, null)!;
        Assert.IsTrue(user.Contains("三字段 T2VA"), "user 应声明三字段 T2VA 输出要求");
        Assert.IsTrue(user.Contains("综合多模态描述:"), "user 字段标题应随输出语言为中文");
    }

    [TestMethod]
    public void BuildShotUserKeepsAssemblerWithRef()
    {
        var dir = NewTempAssetDir("短发女人.png");
        try
        {
            var shot = NewShot("1-1", "场1", 6);
            var media = (List<string>)Invoke("ResolveShotRefImages", NewCtx(NovelOutputFormat.H3FullReference, enableNoRefT2VA: true, assetDir: dir), shot)!;
            var user = (string)Invoke("BuildShotUser", NewCtx(NovelOutputFormat.H3FullReference, enableNoRefT2VA: true, assetDir: dir), shot, (ShotItem?)null, 0, media)!;
            Assert.IsTrue(user.Contains("【任务】"), "有参考图时 user 应走六段式组装器");
            Assert.IsFalse(user.Contains("三字段 T2VA"), "有参考图时不应出现 T2VA 指令");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ResolveShotRefImagesExactAndFallbackMatch()
    {
        var dir = NewTempAssetDir("短发女人.png", "雨夜街道.jpg");
        try
        {
            var ctx = NewCtx(NovelOutputFormat.H3FullReference, assetDir: dir);
            var shot = NewShot("1-1", "场1", 6);
            shot.AssetsUsed = "短发女人、雨夜街道";
            var media = (List<string>)Invoke("ResolveShotRefImages", ctx, shot)!;
            Assert.AreEqual(2, media.Count, "应按资产名解析出两张图");
            Assert.IsTrue(media[0].EndsWith("短发女人.png"), "应优先精确匹配文件名");

            // 变体资产名（不同状态）无独立资产图 → 模糊回退基础资产图
            shot.AssetsUsed = "短发女人(变体)";
            var media2 = (List<string>)Invoke("ResolveShotRefImages", ctx, shot)!;
            Assert.AreEqual(1, media2.Count, "变体资产名应回退到基础资产图");
            Assert.IsTrue(media2[0].EndsWith("短发女人.png"), "回退应命中基础资产图");

            // 无匹配资产名 → 空
            shot.AssetsUsed = "不存在的资产";
            var media3 = (List<string>)Invoke("ResolveShotRefImages", ctx, shot)!;
            Assert.AreEqual(0, media3.Count, "无匹配资产名应返回空");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void DirectorInjectsRefImagesAsR2v()
    {
        var dir = NewTempAssetDir("短发女人.png", "雨夜街道.jpg");
        try
        {
            var ctx = NewCtx(NovelOutputFormat.H3DirectorStory, assetDir: dir);
            var s1 = NewShot("2-1", "场2", 6, "走入雨幕", "再等我五分钟");
            var s2 = NewShot("2-2", "场2", 8, "回头微笑", "好");
            s2.AssetsUsed = "短发女人、雨夜街道";   // 本批镜头引用两张资产图 → 导演台应注入 <Picture 1>/<Picture 2>
            var shots = new List<ShotItem> { s1, s2 };
            var (system, user) = ((string System, string User))Invoke("BuildH3DirectorMessages", ctx, "场2", shots, (string?)null, 1)!;
            // 导演台应注入资产图参考标签（r2v 参考图锁角色；标签以 <Picture N> 列表形式输出）
            Assert.IsTrue(system.Contains("用户已附带 2 个参考素材"), "导演台 system 应声明参考素材数量");
            Assert.IsTrue(system.Contains("<Picture 1>"), "导演台 system 应注入 <Picture 1> 参考标签");
            Assert.IsTrue(system.Contains("<Picture 2>"), "导演台 system 应注入 <Picture 2> 参考标签");
            Assert.IsTrue(user.Contains("每段时长参考约 5 秒"), "导演台段时长应为参考值口径");
        }
        finally { Directory.Delete(dir, true); }
    }
}
