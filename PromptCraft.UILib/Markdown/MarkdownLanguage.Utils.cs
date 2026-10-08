namespace PromptCraft.UILib.Markdown
{
    public class MarkdownLanguage
    {
        private readonly static Dictionary<string, string> _languageMap = new Dictionary<string, string>() { { "c#", "csharp" }, { "C#", "csharp" } };
        public static string GetLanguage(string lan)
        {
            if (_languageMap.TryGetValue(lan, out var language))
            {
                return language;
            }
            return lan;
        }
    }
}
