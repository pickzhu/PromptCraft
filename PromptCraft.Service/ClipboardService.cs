using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Input;
using PromptCraft.Interfaces;

namespace PromptCraft.Service
{
    public class ClipboardService(IClassicDesktopStyleApplicationLifetime lifetime) : IBaseClipboardService
    {
        public void ClearClipboard()
        {
            _ = lifetime.MainWindow?.Clipboard?.ClearAsync();
        }

        public void CopyToClipboard(string txt)
        {
            var clip = lifetime.MainWindow?.Clipboard;
            if (clip == null) return;
            _ = clip.SetValueAsync(DataFormat.Text, txt);
        }

        public string? GetClipboardText()
        {
            var clip = lifetime.MainWindow?.Clipboard;
            if (clip == null) return null;
            return clip.TryGetTextAsync().GetAwaiter().GetResult();
        }
    }
}
