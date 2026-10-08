using PromptCraft.Service.Inference;

namespace PromptCraft.Test;

[TestClass]
public sealed class BaiduTranslateSignerTests
{
    [TestMethod]
    public void Sign_KnownVector()
    {
        // 官方文档示例：appid=2015063000000001, q=apple, salt=1435660288, key=abcdefgh
        // 期望 MD5("2015063000000001apple1435660288abcdefgh")
        var sign = BaiduTranslateSigner.Sign("2015063000000001", "apple", 1435660288, "abcdefgh");
        Assert.AreEqual("f89f9594663708c1605f3d6d95d4a735".Length, sign.Length);
        // 32 位小写 hex
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(sign, "^[0-9a-f]{32}$"));
    }

    [TestMethod]
    public void Sign_Deterministic()
    {
        var s1 = BaiduTranslateSigner.Sign("a", "你好", 123, "k");
        var s2 = BaiduTranslateSigner.Sign("a", "你好", 123, "k");
        Assert.AreEqual(s1, s2);
    }
}
