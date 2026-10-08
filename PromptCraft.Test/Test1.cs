namespace PromptCraft.Test
{
    [TestClass]
    public sealed class Test1
    {
        [TestMethod]
        public void TestMethod1()
        {
            string a1 = "```json\r\n{\r\n\t\"111\":\"123\"\r\n\t\r\n}\r\n```\r\n\r\n```csharp\r\npublic static void Main()\r\n{\r\n\tSystem.Console.WriteLine(\"1234\");\r\n\t\r\n}\r\n```";
            var res = a1.Substring(29, 1);
            Console.WriteLine(res);
        }
    }
}
