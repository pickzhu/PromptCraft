using System;
using System.Collections.Generic;

namespace PromptCraft.Service;

/// <summary>
/// ComfyUI 输出媒体类型判定（图片/视频），供 PromptCraft.Service 与 PromptCraft（UI 侧）共用，
/// 避免各项目重复硬编码扩展名集合。
/// </summary>
public static class ComfyMediaKinds
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mkv", ".mov"
    };

    public static bool IsImage(string pathOrName)
        => !string.IsNullOrWhiteSpace(pathOrName) && ImageExtensions.Contains(System.IO.Path.GetExtension(pathOrName));

    public static bool IsVideo(string pathOrName)
        => !string.IsNullOrWhiteSpace(pathOrName) && VideoExtensions.Contains(System.IO.Path.GetExtension(pathOrName));

    public static bool IsMedia(string pathOrName)
        => IsImage(pathOrName) || IsVideo(pathOrName);
}
