namespace PromptCraft.Interfaces
{
    public interface IBaseClipboardService
    {
        /// <summary>
        /// 清空剪切板
        /// </summary>
        public void ClearClipboard();
        /// <summary>
        /// 复制内容到剪切板
        /// </summary>
        /// <param name="txt"></param>
        public void CopyToClipboard(string txt);
        /// <summary>
        /// 获取剪切板内容
        /// </summary>
        /// <returns></returns>
        public string? GetClipboardText();
    }
}
