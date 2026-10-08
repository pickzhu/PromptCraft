namespace PromptCraft.Interfaces
{
    public interface IBaseNotice
    {
        /// <summary>
        /// 注册事件监听
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="handler"></param>
        public void Subscribe(string eventName, Action<object?> handler);
        /// <summary>
        /// 取消事件监听
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="handler"></param>
        public void Unsubscribe(string eventName, Action<object?> handler);
        /// <summary>
        /// 触发事件
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="data"></param>
        public void Publish(string eventName, object? data = null);
    }
}
