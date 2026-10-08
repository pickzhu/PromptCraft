using PromptCraft.Models.Inference;
using Ke.Bee.Localization.Localizer;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 视频均匀抽帧（对齐 prompt_master.js _extractVideoFramesBase64：394-447）。
/// - ffmpeg 定位：ffmpeg-static 内置 → 回退 PATH（_getFfmpegPath 等价；PromptCraft 未随附 ffmpeg，先探测 PATH+常见路径）
/// - 帧数 count = clamp(maxFrames, 3, 12)
/// - 时长探测 _probeVideoDurationSec：ffmpeg -i 解析 stderr "Duration: hh:mm:ss.xx"，失败按 10s
/// - 均匀抽帧 ss = duration * (i+1) / (count+1)；单帧超时 120s；逐帧 try/catch 跳过；临时目录 finally 清理
/// - 全失败抛「无法从视频提取画面帧…」
/// 无 ffmpeg 时 ExtractFramesBase64Async 返回空列表（调用方 fallback path-only）。
/// </summary>
public static class VideoFrameExtractor
{
    private static readonly string? FfmpegPath = LocateFfmpeg();

    /// <summary>本机是否可抽帧（找到 ffmpeg）。</summary>
    public static bool IsAvailable => FfmpegPath != null;

    private static string? LocateFfmpeg()
    {
        // 1) PATH（ffmpeg / ffmpeg.exe）
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                var p = Path.Combine(dir.Trim(), name);
                if (File.Exists(p)) return p;
            }
        }
        // 2) 常见安装位置（ffmpeg-static 安装、WinGet、scoop 等）
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "scoop", "apps", "ffmpeg", "current", "bin", "ffmpeg.exe"),
            Path.Combine(home, "AppData", "Local", "Microsoft", "WinGet", "Packages",
                "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe", "ffmpeg-*", "bin", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
        };
        foreach (var pattern in candidates)
        {
            if (pattern.Contains('*'))
            {
                var expanded = SafeExpand(pattern);
                if (expanded != null) return expanded;
            }
            else if (File.Exists(pattern))
            {
                return pattern;
            }
        }
        // 3) 常见第三方软件自带 ffmpeg（FDM / 剪映，任意盘根下的固定相对布局）
        foreach (var drive in new[] { "C", "D", "E", "F", "G" })
        {
            var root = $@"{drive}:\";
            var fdm = Path.Combine(root, "soft", "Free Download Manager", "ffmpeg.exe");
            if (File.Exists(fdm)) return fdm;
            var jyDir = Path.Combine(root, "soft", "jian_ying", "JianyingPro");
            if (Directory.Exists(jyDir))
            {
                foreach (var verDir in Directory.EnumerateDirectories(jyDir))
                {
                    var jy = Path.Combine(verDir, "ffmpeg.exe");
                    if (File.Exists(jy)) return jy;
                }
            }
        }
        return null;
    }

    private static string? SafeExpand(string pattern)
    {
        try
        {
            var dir = Path.GetDirectoryName(pattern) ?? "";
            var file = Path.GetFileName(pattern);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, file).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ffmpeg -i 探测时长（stderr 解析 Duration: hh:mm:ss.xx）；失败返回 0。</summary>
    internal static async Task<double> ProbeDurationSecAsync(string ffmpegPath, string videoPath, CancellationToken ct = default)
    {
        try
        {
            var run = await RunAsync(ffmpegPath,
                new[] { "-hide_banner", "-i", videoPath }, TimeSpan.FromSeconds(20), ct);
            var errOut = run.stderr ?? "";
            var m = Regex.Match(errOut, @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)");
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value) * 3600.0
                    + int.Parse(m.Groups[2].Value) * 60.0
                    + int.Parse(m.Groups[3].Value)
                    + double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // 无输出参数时 ffmpeg 以非 0 退出并带 Duration 到 stderr；RunAsync 失败也继续尝试解析
        }
        return 0;
    }

    /// <summary>
    /// 均匀抽帧为 JPEG base64 列表（count = clamp(maxFrames,3,12)；对齐 _extractVideoFramesBase64）。
    /// 无 ffmpeg → 返回空列表；全部帧失败 → 抛 InferencesException（提示换模型）。
    /// </summary>
    public static async Task<List<string>> ExtractFramesBase64Async(string videoPath, int maxFrames, CancellationToken ct = default)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return new List<string>();

        var count = Math.Clamp(maxFrames, 3, 12);
        var duration = await ProbeDurationSecAsync(ffmpeg, videoPath, ct);
        if (duration < 0.5) duration = 10;

        var tmpDir = Path.Combine(Path.GetTempPath(), "fn-vid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var frames = new List<string>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var ss = Math.Max(0, duration * (i + 1) / (count + 1));
                var outPath = Path.Combine(tmpDir, $"frame_{i}.jpg");
                try
                {
                    await RunAsync(ffmpeg, new[]
                    {
                        "-hide_banner", "-loglevel", "error",
                        "-ss", ss.ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", videoPath,
                        "-vframes", "1", "-q:v", "3", "-y", outPath,
                    }, TimeSpan.FromSeconds(120), ct);
                    if (File.Exists(outPath))
                        frames.Add(Convert.ToBase64String(File.ReadAllBytes(outPath)));
                }
                catch
                {
                    // 单帧失败则跳过（对齐逐帧 try/catch）
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { /* ignore */ }
        }
        if (frames.Count == 0)
        {
            throw new InferencesException(InferenceErrorKind.NotSupported,
                Localizer.Instance?["VideoExtractFailed"] ?? "");
        }
        return frames;
    }

    /// <summary>
    /// 抽 1 帧存为 JPEG 文件并返回路径（卡片缩略图用；无 ffmpeg → null）。
    /// 与 ExtractFramesBase64Async 同一套 ffmpeg 定位与超时策略。
    /// </summary>
    public static async Task<string?> ExtractFirstFramePathAsync(string videoPath, CancellationToken ct = default)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return null;
        var duration = await ProbeDurationSecAsync(ffmpeg, videoPath, ct);
        if (duration < 0.5) duration = 10;
        var outPath = Path.Combine(Path.GetTempPath(), $"fn-vid-{Guid.NewGuid():N}-f0.jpg");
        try
        {
            // 取视频 1/3 处一帧，避免纯黑片头
            var ss = Math.Max(0, duration / 3.0);
            await RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", ss.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", videoPath,
                "-vframes", "1", "-q:v", "5", "-y", outPath,
            }, TimeSpan.FromSeconds(60), ct);
            return File.Exists(outPath) ? outPath : null;
        }
        catch
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            return null;
        }
    }

    /// <summary>执行 ffmpeg 并捕获 stderr；超时杀进程并抛错。</summary>
    private static async Task<(string stdout, string stderr, int code)> RunAsync(
        string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        if (!proc.Start()) throw new InvalidOperationException(Localizer.Instance?["FfmpegStartFailed"] ?? "");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InferencesException(InferenceErrorKind.TimeoutError, Localizer.Instance?["FfmpegFrameTimeout"] ?? "");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (stdout, stderr, proc.ExitCode);
    }
}
