using System.Reflection;
using PromptCraft.Models.Inference.Novel;
using PromptCraft.Service.Inference.Novel;

namespace PromptCraft.Test;

// 小说页 S7 复用扩写页 MiniMaxAssembler（pe_expand_h3_full_reference / pe_expand_minimax_continuous_story）：
// 验证「Minimax六段式通用提示词」「连续剧情（导演台）」的输出契约与扩写页一致。
[TestClass]
public sealed class NovelMiniMaxFormatTests
{
    private static NovelPipelineContext NewCtx(NovelOutputFormat format, NovelOutputLanguage lang = NovelOutputLanguage.Chinese) => new()
    {
        Characters = "短发女人：三十岁，黑色短发，穿深灰风衣；男人：四十岁，络腮胡，穿皮夹克。",
        ConfirmedAssets = { "短发女人（角色）", "雨夜城市街道（场景）" },
        Options = new NovelPromptOptions
        {
            OutputFormat = format,
            OutputLanguage = lang,
            AspectRatio = "16:9",
            DefaultShotSeconds = 6,
        },
    };

    private static ShotItem NewShot(string id, string scene, double dur, string action, string dialogue) => new()
    {
        ShotId = id,
        SourceScene = scene,
        Purpose = "建立情绪",
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
        FirstFrame = "雨夜街头，短发女人站在路灯下，风衣被风吹动。",
        LastFrame = "短发女人转身，走入雨幕。",
        ContinuityRisk = "无",
    };

    private static object? Invoke(string methodName, params object[] args)
    {
        var m = typeof(NovelToPromptService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(m, $"私有方法 {methodName} 不存在");
        return m!.Invoke(null, args);
    }

    [TestMethod]
    public void FullReferenceSystemMatchesExpandSixSectionContract()
    {
        var ctx = NewCtx(NovelOutputFormat.H3FullReference);
        var system = (string)Invoke("BuildShotSystem", ctx, NewShot("1-1", "场1", 6, "行走", "你好"), false, null)!;
        // 六段式标题（中文强制），与扩写页 full_reference 一致
        Assert.IsTrue(system.Contains("主体定义"), "system 应含中文六段标题 主体定义");
        Assert.IsTrue(system.Contains("摘要"), "system 应含 摘要");
        Assert.IsTrue(system.Contains("保留分析"));
        Assert.IsTrue(system.Contains("详细描述"));
        Assert.IsTrue(system.Contains("整体声景"));
        Assert.IsTrue(system.Contains("非叙事配乐"));
        Assert.IsTrue(system.Contains("场景：Minimax六段式通用提示词（full_reference）"), "system 应引用扩写页同一场景（full_reference）");
        Assert.IsTrue(system.Contains("绝对纯净输出"), "system 应含纯输出要求");
        // 导演台契约不应出现在六段式 system 中
        Assert.IsFalse(system.Contains("===== 提示词组"), "六段式 system 不应含提示词组分隔行");
    }

    [TestMethod]
    public void FullReferenceUserCarriesShotShortText()
    {
        var ctx = NewCtx(NovelOutputFormat.H3FullReference);
        var user = (string)Invoke("BuildShotUser", ctx, NewShot("1-1", "场1", 6, "行走", "你好"), null, 0, null)!;
        Assert.IsTrue(user.Contains("【本镜头（单镜头）】"), "user 应含单镜头内容");
        Assert.IsTrue(user.Contains("雨夜街头，短发女人站在路灯下"), "user 应含首帧硬描述");
        Assert.IsTrue(user.Contains("短发女人：三十岁"), "user 应含角色圣经");
        Assert.IsTrue(user.Contains("【任务】"), "user 应含组装器任务指令");
    }

    [TestMethod]
    public void DirectorMessagesContract()
    {
        var ctx = NewCtx(NovelOutputFormat.H3DirectorStory);
        var shots = new List<ShotItem>
        {
            NewShot("2-1", "场2", 6, "走入雨幕", "再等我五分钟"),
            NewShot("2-2", "场2", 8, "回头微笑", "好"),
        };
        var (system, user) = ((string System, string User))Invoke("BuildH3DirectorMessages", ctx, "场2", shots, (string?)null, 1)!;

        // 导演台：公共设定 + N 组提示词组（与扩写页 continuous_story 一致）
        Assert.IsTrue(system.Contains("===== 公共设定 ====="), "system 应含 公共设定 分隔行");
        Assert.IsTrue(system.Contains("===== 提示词组 k ====="), "system 应含 提示词组 分隔行模板");
        Assert.IsTrue(system.Contains("场景：连续剧情（导演台）（continuous_story）"), "system 应引用扩写页同一场景（continuous_story）");
        Assert.IsTrue(system.Contains("第 2 组起"), "system 应含组间无硬切规则");
        Assert.IsTrue(system.Contains("禁止再写「主体定义:」"), "system 应禁止组内重复主体定义");

        // user：表单参数逐段填入（每组 = 一个镜头），角色圣经进入创作需求
        Assert.IsTrue(user.Contains("第 1 段在干什么"), "表单应含第 1 段在干什么");
        Assert.IsTrue(user.Contains("第 2 段在干什么"), "表单应含第 2 段在干什么");
        Assert.IsTrue(user.Contains("再等我五分钟"), "第 1 段应含镜头对白");
        Assert.IsTrue(user.Contains("【本场创作需求】场次 场2"), "user 应含本场创作需求");
        Assert.IsTrue(user.Contains("短发女人：三十岁"), "user 应含角色圣经（供公共设定主体定义）");
    }

    [TestMethod]
    public void DirectorCrossBatchHandoff()
    {
        var ctx = NewCtx(NovelOutputFormat.H3DirectorStory);
        var shots = new List<ShotItem> { NewShot("3-1", "场3", 6, "奔跑", "快跑"), NewShot("3-2", "场3", 6, "跌倒", "啊") };
        var (_, user) = ((string System, string User))Invoke("BuildH3DirectorMessages", ctx, "场3", shots, "男人倒在雨地里。", 1)!;
        Assert.IsTrue(user.Contains("【承接上一批】上一批末段停在：男人倒在雨地里。"), "跨批应注入上一批停帧衔接");
        Assert.IsTrue(user.Contains("无硬切。紧接上一段"), "跨批应要求无硬切衔接");
    }

    [TestMethod]
    public void DirectorSegmentSecondsRoundedToAllowed()
    {
        var ctx = NewCtx(NovelOutputFormat.H3DirectorStory);
        var shots = new List<ShotItem>
        {
            NewShot("4-1", "场4", 4, "a", ""),
            NewShot("4-2", "场4", 6, "b", ""),
        }; // 平均 5s → 5
        var (_, user) = ((string System, string User))Invoke("BuildH3DirectorMessages", ctx, "场4", shots, (string?)null, 1)!;
        Assert.IsTrue(user.Contains("每段时长: 约 5 秒"), "平均 5s 应取 5 秒档");

        var shots2 = new List<ShotItem>
        {
            NewShot("4-3", "场4", 12, "a", ""),
            NewShot("4-4", "场4", 14, "b", ""),
        }; // 平均 13s → 15
        var (_, user2) = ((string System, string User))Invoke("BuildH3DirectorMessages", ctx, "场4", shots2, (string?)null, 1)!;
        Assert.IsTrue(user2.Contains("每段时长: 约 15 秒"), "平均 13s 应就近取 15 秒档");
    }

    [TestMethod]
    public void DirectorFirstBatch_CommonSettingCompleteAndOnce()
    {
        var ctx = NewCtx(NovelOutputFormat.H3DirectorStory);
        var shots = new List<ShotItem> { NewShot("5-1", "场5", 6, "a", ""), NewShot("5-2", "场5", 6, "b", "") };
        var (system, user) = ((string System, string User))Invoke("BuildH3DirectorMessages", ctx, "场5", shots, (string?)null, 1)!;
        // 首批：公共设定完整性指令存在；无「后续批次」说明
        Assert.IsTrue(system.Contains("【公共设定完整性·最高优先级】"), "首批应要求公共设定完整");
        Assert.IsFalse(system.Contains("【本批补充·最高优先级】"), "首批不应含后续批次覆盖指令");
        Assert.IsFalse(user.Contains("【批次说明】"), "首批 user 不应标为后续批次");
    }

    [TestMethod]
    public void DirectorSubsequentBatch_OmitsCommonSettingAndKeepsNumbering()
    {
        var ctx = NewCtx(NovelOutputFormat.H3DirectorStory);
        var shots = Enumerable.Range(1, 8).Select(i => NewShot($"6-{i}", "场6", 6, "a", "")).ToList();
        var (system, user) = ((string System, string User))Invoke("BuildH3DirectorMessages", ctx, "场6", shots, "男人倒地。", 9)!;
        // 后续批次：覆盖公共设定输出（只续写提示词组），编号按全场连续 9..16，不重复公共设定
        Assert.IsTrue(system.Contains("【本批补充·最高优先级】"), "后续批应含覆盖指令");
        Assert.IsTrue(system.Contains("提示词组 9"), "后续批应从第 9 组开始");
        Assert.IsTrue(system.Contains("第 9 至第 16 组"), "后续批应标注全场连续编号范围");
        Assert.IsFalse(system.Contains("【公共设定完整性·最高优先级】"), "后续批不应再要求输出公共设定");
        Assert.IsTrue(user.Contains("【批次说明】"), "后续批 user 应标注批次性质");
        Assert.IsTrue(user.Contains("【承接上一批】"), "后续批仍应注入上一批停帧衔接");
    }
}
