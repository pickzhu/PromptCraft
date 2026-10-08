using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer;

namespace PromptCraft.Service
{
    public class NoticeService : IBaseNotice
    {
        private static readonly Lazy<IBaseNotice> _instance =
        new Lazy<IBaseNotice>(() => new NoticeService());
        public static IBaseNotice Instance => _instance.Value;

        private readonly Dictionary<string, List<Action<object?>>> _eventHandlers
        = new Dictionary<string, List<Action<object?>>>();

        private readonly object _lock = new object();

        /// <summary>
        /// 注册事件监听
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="handler"></param>
        public void Subscribe(string eventName, Action<object?> handler)
        {
            lock (_lock)
            {
                if (!_eventHandlers.ContainsKey(eventName))
                    _eventHandlers[eventName] = new List<Action<object?>>();

                _eventHandlers[eventName].Add(handler);
            }
        }

        /// <summary>
        /// 取消事件监听
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="handler"></param>
        public void Unsubscribe(string eventName, Action<object?> handler)
        {
            lock (_lock)
            {
                if (_eventHandlers.ContainsKey(eventName))
                    _eventHandlers[eventName].Remove(handler);
            }
        }

        /// <summary>
        /// 触发事件
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="data"></param>
        public void Publish(string eventName, object? data = null)
        {
            List<Action<object?>> handlersCopy = new();
            lock (_lock)
            {
                if (!_eventHandlers.ContainsKey(eventName)) return;
                handlersCopy = new List<Action<object?>>(_eventHandlers[eventName] ?? new());
            }

            foreach (var handler in handlersCopy)
            {
                RunHandle(eventName, handler, data);
            }
        }

        private void RunHandle(string eventName, Action<object?> handler, object? data)
        {
            try
            {
                handler.Invoke(data);
            }
            catch (Exception ex)
            {
                // 事件处理器异常：记录并收集，避免吞掉错误导致难以排查
                LogService.Instance.Error(string.Format(Localizer.Instance?["EventHandlerException"] ?? "", eventName), "Notice", ex);
            }
        }
    }
}
