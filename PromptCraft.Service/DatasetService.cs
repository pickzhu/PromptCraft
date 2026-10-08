using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer;
using System.Text.Json;

namespace PromptCraft.Service;

/// <summary>数据集素材来源模式（对齐 PromptMaster material_mode）。</summary>
public enum DatasetMaterialMode { Managed, ScanPath }

/// <summary>数据集元信息（对齐 pm_datasets.js info.json 字段）。</summary>
public sealed class DatasetInfo
{
    public string MaterialMode { get; set; } = "managed";      // managed | scan_path
    public string ScanPathRoot { get; set; } = "";
    public bool ScanRecursive { get; set; } = true;
    public bool OverwriteSidecar { get; set; }
    public string CaptionFileFormat { get; set; } = "txt";     // txt | json
}

/// <summary>数据集列表行（对齐 getList 返回项）。</summary>
public sealed class DatasetListItem
{
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string MaterialMode { get; set; } = "managed";
    public int ImageCount { get; set; }
    public int VideoCount { get; set; }
    public int TotalCount => ImageCount + VideoCount;
}

/// <summary>数据集内单个素材（对齐 getImages 返回项）。</summary>
public sealed class DatasetMediaItem
{
    public string Base { get; set; } = "";
    public string File { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string TextContent { get; set; } = "";
    public string TextPath { get; set; } = "";
    public string JsonPath { get; set; } = "";
    public string MediaKind { get; set; } = "image";           // image | video
}

/// <summary>
/// 模型训练数据集服务（对齐 PromptMaster pm_datasets.js）：
/// 数据集为磁盘文件制（workspace/datasets/&lt;name&gt;/info.json + 媒体 + 侧车 caption）。
/// 批量打标复用 <see cref="ReverseCaptionService.CaptionBatchAsync"/>（train 工程经 ApplyReverseToCaption 映射）。
/// </summary>
public sealed class DatasetService
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".wmv", ".flv", ".ts" };
    private static readonly HashSet<string> ImportExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".wmv", ".flv", ".ts",
        ".txt", ".json",
    };

    private readonly IWorkspaceService _workspace;
    private readonly IReverseCaptionService _reverse;
    private readonly PmPromptEngineeringService _peService;
    private readonly IBaseLogService _log;

    /// <summary>中文语言感知排序比较器（对齐 PromptMaster localeCompare zh-CN）。</summary>
    private static readonly StringComparer ZhComparer =
        StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true);

    public DatasetService(
        IWorkspaceService workspace,
        IReverseCaptionService reverse,
        PmPromptEngineeringService peService,
        IBaseLogService? log = null)
    {
        _workspace = workspace;
        _reverse = reverse;
        _peService = peService;
        _log = log ?? PromptCraft.Service.LogService.Instance;
    }

    // ==================== 路径解析 ====================

    private bool TryGetDatasetsRoot(out string root, out string message)
    {
        var ws = _workspace.Root;
        if (string.IsNullOrWhiteSpace(ws))
        {
            root = "";
            message = Localizer.Instance?["DatasetWorkspaceRequired"] ?? "尚未设置工作空间目录，请在系统设置中设置工作空间目录！";
            return false;
        }
        root = _workspace.DatasetsDir;
        Directory.CreateDirectory(root);
        message = "";
        return true;
    }

    private bool TryGetDatasetDir(string? name, out string dir, out string message)
    {
        if (!TryGetDatasetsRoot(out var root, out message))
        {
            dir = "";
            return false;
        }
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || n.Contains(' '))
        {
            dir = "";
            message = Localizer.Instance?["DatasetInvalidName"] ?? "数据集名字不能为空，且不能包含空格与非法字符";
            return false;
        }
        dir = Path.Combine(root, n);
        message = "";
        return true;
    }

    private static bool IsMediaFile(string name)
        => ImageExts.Contains(Path.GetExtension(name)) || VideoExts.Contains(Path.GetExtension(name));

    private static string MediaKindForPath(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (VideoExts.Contains(ext)) return "video";
        if (ImageExts.Contains(ext)) return "image";
        return "";
    }

    private static DatasetInfo ReadInfo(string datasetDir)
    {
        var infoPath = Path.Combine(datasetDir, "info.json");
        var info = new DatasetInfo();
        if (!File.Exists(infoPath)) return info;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
            var r = doc.RootElement;
            if (r.TryGetProperty("material_mode", out var mm)) info.MaterialMode = mm.GetString() == "scan_path" ? "scan_path" : "managed";
            if (r.TryGetProperty("scan_path_root", out var sp)) info.ScanPathRoot = sp.GetString() ?? "";
            if (r.TryGetProperty("scan_recursive", out var sr)) info.ScanRecursive = sr.ValueKind == JsonValueKind.False ? false : true;
            if (r.TryGetProperty("overwrite_sidecar", out var ov)) info.OverwriteSidecar = ov.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("caption_file_format", out var cf)) info.CaptionFileFormat = (cf.GetString() ?? "txt").ToLowerInvariant() == "json" ? "json" : "txt";
            else if (r.TryGetProperty("caption_prefs", out var cp) && cp.ValueKind == JsonValueKind.Object
                     && cp.TryGetProperty("caption_file_format", out var cpf))
                info.CaptionFileFormat = (cpf.GetString() ?? "txt").ToLowerInvariant() == "json" ? "json" : "txt";
        }
        catch { /* ignore */ }
        return info;
    }

    private static void WriteInfo(string datasetDir, DatasetInfo info)
    {
        var payload = new Dictionary<string, object?>
        {
            ["material_mode"] = info.MaterialMode,
            ["scan_path_root"] = info.ScanPathRoot,
            ["scan_recursive"] = info.ScanRecursive,
            ["overwrite_sidecar"] = info.OverwriteSidecar,
            ["caption_file_format"] = info.CaptionFileFormat,
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(datasetDir, "info.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (int Image, int Video) CountDatasetFolderMedia(string folder)
    {
        var img = 0;
        var vid = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ImageExts.Contains(ext)) img++;
                else if (VideoExts.Contains(ext)) vid++;
            }
        }
        catch { /* ignore */ }
        return (img, vid);
    }

    private List<string> CollectMediaOnly(string dir, string? mediaTarget, bool recursive, List<string>? acc = null)
    {
        acc ??= new List<string>();
        try
        {
            foreach (var ent in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(ent))
                {
                    if (recursive) CollectMediaOnly(ent, mediaTarget, true, acc);
                    continue;
                }
                if (!IsMediaFile(ent)) continue;
                var kind = MediaKindForPath(ent);
                var ok = mediaTarget switch
                {
                    "video" => kind == "video",
                    "image" => kind == "image",
                    _ => kind.Length > 0,
                };
                if (ok) acc.Add(Path.GetFullPath(ent));
            }
        }
        catch { /* ignore */ }
        return acc;
    }

    private static string SidecarForMedia(string filePath, string captionFileFormat)
    {
        var useJson = (captionFileFormat ?? "txt").ToLowerInvariant() == "json";
        var dir = Path.GetDirectoryName(filePath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        return Path.Combine(dir, baseName + (useJson ? ".json" : ".txt"));
    }

    private static bool HasNonEmptySidecar(string filePath, string captionFileFormat)
    {
        var sidecar = SidecarForMedia(filePath, captionFileFormat);
        if (!File.Exists(sidecar)) return false;
        try { return File.ReadAllText(sidecar).Trim().Length > 0; }
        catch { return false; }
    }

    private List<string> FilterCaptionTargets(IEnumerable<string> paths, string? mediaTarget, bool overwriteSidecar, string captionFileFormat)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in paths)
        {
            var p = Path.GetFullPath((raw ?? "").Trim());
            if (p.Length == 0 || !File.Exists(p) || !seen.Add(p)) continue;
            var kind = MediaKindForPath(p);
            if (kind.Length == 0) continue;
            var ok = mediaTarget switch
            {
                "video" => kind == "video",
                "image" => kind == "image",
                _ => true,
            };
            if (!ok) continue;
            if (!overwriteSidecar && HasNonEmptySidecar(p, captionFileFormat)) continue;
            result.Add(p);
        }
        result.Sort(StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true));
        return result;
    }

    // ==================== 数据集 CRUD ====================

    public (bool Ok, string Message) Add(string? name)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, message);
        if (Directory.Exists(dir)) return (false, Localizer.Instance?["DatasetExists"] ?? "数据集已存在！");
        Directory.CreateDirectory(dir);
        return (true, "");
    }

    public (bool Ok, string Message) Rename(string? oldName, string? newName)
    {
        if (!TryGetDatasetDir(oldName, out var oldPath, out var msg)) return (false, msg);
        if (!TryGetDatasetDir(newName, out var newPath, out _)) return (false, Localizer.Instance?["DatasetInvalidName"] ?? "数据集名字不能为空，且不能包含空格与非法字符");
        if (!Directory.Exists(oldPath)) return (false, Localizer.Instance?["DatasetNotExist"] ?? "原数据集不存在");
        if (Directory.Exists(newPath)) return (false, Localizer.Instance?["DatasetExists"] ?? "目标数据集已存在");
        Directory.Move(oldPath, newPath);
        return (true, "");
    }

    public (bool Ok, string Message) Delete(string? name)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, message);
        if (!Directory.Exists(dir)) return (false, Localizer.Instance?["DatasetNotExist"] ?? "数据集不存在");
        try
        {
            Directory.Delete(dir, true);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public IReadOnlyList<DatasetListItem> List(string? keyword = null)
    {
        if (!TryGetDatasetsRoot(out var root, out _)) return new List<DatasetListItem>();
        var result = new List<DatasetListItem>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(keyword) && !name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            var info = ReadInfo(dir);
            var (img, vid) = info.MaterialMode == "scan_path" && !string.IsNullOrEmpty(info.ScanPathRoot)
                ? CountScanPathMedia(info.ScanPathRoot, info.ScanRecursive)
                : CountDatasetFolderMedia(dir);
            result.Add(new DatasetListItem
            {
                Name = name,
                LocalPath = Path.GetFullPath(dir),
                MaterialMode = info.MaterialMode,
                ImageCount = img,
                VideoCount = vid,
            });
        }
        result.Sort((a, b) => ZhComparer.Compare(a.Name, b.Name));
        return result;
    }

    private (int Image, int Video) CountScanPathMedia(string scanRoot, bool recursive)
    {
        if (string.IsNullOrEmpty(scanRoot) || !Directory.Exists(scanRoot)) return (0, 0);
        var paths = CollectMediaOnly(scanRoot, "mixed", recursive);
        var img = 0;
        var vid = 0;
        foreach (var p in paths)
        {
            if (MediaKindForPath(p) == "image") img++;
            else vid++;
        }
        return (img, vid);
    }

    public DatasetInfo GetInfo(string? name)
    {
        if (!TryGetDatasetDir(name, out var dir, out _) || !Directory.Exists(dir)) return new DatasetInfo();
        return ReadInfo(dir);
    }

    /// <summary>解析数据集文件夹绝对路径（UI 打开文件夹用）。</summary>
    public (bool Ok, string Dir, string Message) ResolveDatasetDir(string? name)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, "", message);
        return (true, Path.GetFullPath(dir), "");
    }

    public (bool Ok, string Message, DatasetInfo Info) SaveInfo(string? name, DatasetInfo info)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, message, new DatasetInfo());
        if (!Directory.Exists(dir)) return (false, Localizer.Instance?["DatasetNotExist"] ?? "目录不存在", new DatasetInfo());
        WriteInfo(dir, info);
        return (true, "", ReadInfo(dir));
    }

    public IReadOnlyList<DatasetMediaItem> GetImages(string? name, string? captionFileFormat = null)
    {
        if (!TryGetDatasetDir(name, out var dir, out _) || !Directory.Exists(dir)) return new List<DatasetMediaItem>();
        var format = (captionFileFormat ?? ReadInfo(dir).CaptionFileFormat ?? "txt").ToLowerInvariant();
        var preferJson = format == "json";
        var result = new List<DatasetMediaItem>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!IsMediaFile(Path.GetFileName(file))) continue;
            var baseName = Path.GetFileNameWithoutExtension(file);
            var txt = Path.Combine(dir, baseName + ".txt");
            var json = Path.Combine(dir, baseName + ".json");
            var textContent = "";
            var textPath = "";
            if (preferJson)
            {
                if (File.Exists(json)) { textContent = File.ReadAllText(json); textPath = json; }
                else if (File.Exists(txt)) { textContent = File.ReadAllText(txt); textPath = txt; }
            }
            else
            {
                if (File.Exists(txt)) { textContent = File.ReadAllText(txt); textPath = txt; }
                else if (File.Exists(json)) { textContent = File.ReadAllText(json); textPath = json; }
            }
            result.Add(new DatasetMediaItem
            {
                Base = baseName,
                File = Path.GetFileName(file),
                FilePath = Path.GetFullPath(file),
                TextContent = textContent,
                TextPath = textPath,
                JsonPath = File.Exists(json) ? json : "",
                MediaKind = MediaKindForPath(file),
            });
        }
        result.Sort((a, b) => ZhComparer.Compare(a.File, b.File));
        return result;
    }

    // ==================== 素材导入 ====================

    public (bool Ok, int Copied, string Message) AddImagesFromPaths(string? name, IEnumerable<string> paths)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, 0, message);
        Directory.CreateDirectory(dir);
        var valid = paths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (valid.Count == 0) return (false, 0, Localizer.Instance?["DatasetNoValidFiles"] ?? "没有有效文件");
        var copied = 0;
        foreach (var src in valid)
        {
            try
            {
                File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                _log.Warn(string.Format(Localizer.Instance?["DatasetCopyFileFailed"] ?? "导入素材失败: {0}", ex.Message), "Dataset", ex);
            }
        }
        return (true, copied, string.Format(Localizer.Instance?["DatasetImportedFormat"] ?? "已导入 {0} 个素材", copied));
    }

    public (bool Ok, int Copied, string Message) ImportFolder(string? name, string? folderPath)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, 0, message);
        Directory.CreateDirectory(dir);
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return (false, 0, Localizer.Instance?["DatasetFolderNotExist"] ?? "文件夹不存在");
        var collected = new List<string>();
        CollectAllImportable(folderPath, collected);
        if (collected.Count == 0) return (false, 0, Localizer.Instance?["DatasetFolderNoMedia"] ?? "文件夹内没有可导入的图片或视频");
        var copied = 0;
        foreach (var src in collected)
        {
            try
            {
                File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                _log.Warn(string.Format(Localizer.Instance?["DatasetCopyFileFailed"] ?? "导入素材失败: {0}", ex.Message), "Dataset", ex);
            }
        }
        return (true, copied, string.Format(Localizer.Instance?["DatasetImportedFormat"] ?? "已导入 {0} 个素材", copied));
    }

    private static void CollectAllImportable(string dir, List<string> acc)
    {
        try
        {
            foreach (var ent in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(ent)) { CollectAllImportable(ent, acc); continue; }
                if (ImportExts.Contains(Path.GetExtension(ent))) acc.Add(ent);
            }
        }
        catch { /* ignore */ }
    }

    public (bool Ok, int Count, string Message) DeleteImages(IEnumerable<string> paths)
    {
        var count = 0;
        foreach (var p in paths)
        {
            var path = (p ?? "").Trim();
            if (path.Length == 0 || !File.Exists(path)) continue;
            var dir = Path.GetDirectoryName(path) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(path);
            foreach (var sp in new[] { path, Path.Combine(dir, baseName + ".txt"), Path.Combine(dir, baseName + ".json"), Path.Combine(dir, baseName + ".zh.txt"), Path.Combine(dir, baseName + ".en.txt") })
            {
                try { if (File.Exists(sp)) File.Delete(sp); } catch { /* ignore */ }
            }
            count++;
        }
        return (true, count, "");
    }

    public (bool Ok, string Message) SaveMaterialCaption(string? filePath, string text, string? captionFileFormat)
    {
        var path = (filePath ?? "").Trim();
        if (path.Length == 0) return (false, Localizer.Instance?["DatasetMissingFilePath"] ?? "缺少 filePath");
        var dir = Path.GetDirectoryName(path) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(path);
        var useJson = (captionFileFormat ?? "txt").ToLowerInvariant() == "json";
        var target = useJson ? Path.Combine(dir, baseName + ".json") : Path.Combine(dir, baseName + ".txt");
        File.WriteAllText(target, text ?? "");
        return (true, "");
    }

    // ==================== 统一打标 ====================

    public (bool Ok, int Count, string Message) UniformCaption(
        string? name, string? op, string? textContent, string? targetContent, string? captionFileFormat, string? mediaTarget)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, 0, message);
        if (!Directory.Exists(dir)) return (false, 0, Localizer.Instance?["DatasetNotExist"] ?? "目录不存在");
        var fmt = (captionFileFormat ?? "txt").ToLowerInvariant() == "json" ? "json" : "txt";
        var mediaPaths = CollectMediaOnly(dir, mediaTarget, false);
        mediaPaths.Sort(StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true));
        if (mediaPaths.Count == 0) return (false, 0, Localizer.Instance?["DatasetNoUniformTarget"] ?? "没有可统一打标的素材");
        var opKey = string.IsNullOrEmpty(op) ? "cover" : op;
        foreach (var media in mediaPaths)
        {
            ApplyUniform(media, opKey, textContent, targetContent, fmt);
        }
        return (true, mediaPaths.Count, "");
    }

    private static void ApplyUniform(string mediaPath, string op, string? textContent, string? targetContent, string fileFmt)
    {
        var dir = Path.GetDirectoryName(mediaPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(mediaPath);
        var target = fileFmt == "json" ? Path.Combine(dir, baseName + ".json") : Path.Combine(dir, baseName + ".txt");
        var old = File.Exists(target) ? File.ReadAllText(target) : "";
        var content = textContent ?? "";
        if (op != "cover" && old.Length > 0)
        {
            content = op switch
            {
                "start" => content + old,
                "end" => old + content,
                "replace" => old.Replace(content, targetContent ?? ""),
                _ => content,
            };
        }
        File.WriteAllText(target, content);
    }

    // ==================== 扫描打标目标 ====================

    public (bool Ok, string Message, int Matched, int Target, int Skipped, List<string> Paths) ScanCaptionTargets(
        string? name, string? mediaTarget, string? scanPathRoot, bool scanRecursive, bool overwriteSidecar, string? captionFileFormat)
    {
        if (!TryGetDatasetDir(name, out _, out var message)) return (false, message, 0, 0, 0, new List<string>());
        if (string.IsNullOrEmpty(scanPathRoot)) return (false, Localizer.Instance?["DatasetScanRootRequired"] ?? "请先选择扫描目录", 0, 0, 0, new List<string>());
        if (!Directory.Exists(scanPathRoot)) return (false, Localizer.Instance?["DatasetScanRootNotExist"] ?? "扫描目录不存在", 0, 0, 0, new List<string>());
        var allRaw = CollectMediaOnly(scanPathRoot, mediaTarget, scanRecursive);
        var fmt = (captionFileFormat ?? "txt").ToLowerInvariant();
        var allMatched = FilterCaptionTargets(allRaw, mediaTarget, true, fmt);
        var targets = FilterCaptionTargets(allRaw, mediaTarget, overwriteSidecar, fmt);
        return (true, "", allMatched.Count, targets.Count, Math.Max(0, allMatched.Count - targets.Count), targets);
    }

    /// <summary>目录扫描模式下列出扫描到的媒体文件（含 sidecar 打标文本回显），供素材预览区显示缩略图。</summary>
    public IReadOnlyList<DatasetMediaItem> GetScanMedia(
        string? name, string? mediaTarget, string? scanPathRoot, bool scanRecursive, string? captionFileFormat)
    {
        if (!TryGetDatasetDir(name, out _, out _) || string.IsNullOrEmpty(scanPathRoot) || !Directory.Exists(scanPathRoot))
            return new List<DatasetMediaItem>();
        var fmt = (captionFileFormat ?? "txt").ToLowerInvariant();
        var preferJson = fmt == "json";
        var result = new List<DatasetMediaItem>();
        foreach (var file in CollectMediaOnly(scanPathRoot, mediaTarget, scanRecursive))
        {
            var dir = Path.GetDirectoryName(file) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(file);
            var txt = Path.Combine(dir, baseName + ".txt");
            var json = Path.Combine(dir, baseName + ".json");
            var textContent = "";
            if (preferJson)
            {
                if (File.Exists(json)) textContent = File.ReadAllText(json);
                else if (File.Exists(txt)) textContent = File.ReadAllText(txt);
            }
            else
            {
                if (File.Exists(txt)) textContent = File.ReadAllText(txt);
                else if (File.Exists(json)) textContent = File.ReadAllText(json);
            }
            result.Add(new DatasetMediaItem
            {
                Base = baseName,
                File = Path.GetFileName(file),
                FilePath = Path.GetFullPath(file),
                TextContent = textContent,
                TextPath = "",
                JsonPath = File.Exists(json) ? json : "",
                MediaKind = MediaKindForPath(file),
            });
        }
        result.Sort((a, b) => ZhComparer.Compare(a.File, b.File));
        return result;
    }

    // ==================== 批量打标（复用反推服务） ====================

    /// <summary>解析待打标路径（对齐 _collectCaptionPaths）：scan_path 扫外部目录；managed 取选中或全部。返回 (ok,message,paths)。</summary>
    public (bool Ok, string Message, List<string> Paths) ResolveCaptionPaths(
        string? name, string? mediaTarget, bool selectedOnly, IEnumerable<string>? selectedPaths, DatasetInfo info)
    {
        if (!TryGetDatasetDir(name, out var dir, out var message)) return (false, message, new List<string>());
        if (!Directory.Exists(dir)) return (false, Localizer.Instance?["DatasetNotExist"] ?? "目录不存在", new List<string>());
        var fmt = (info.CaptionFileFormat ?? "txt").ToLowerInvariant();
        if (info.MaterialMode == "scan_path")
        {
            var root = info.ScanPathRoot;
            if (string.IsNullOrEmpty(root)) return (false, Localizer.Instance?["DatasetScanRootRequired"] ?? "请先选择扫描目录", new List<string>());
            if (!Directory.Exists(root)) return (false, Localizer.Instance?["DatasetScanRootNotExist"] ?? "扫描目录不存在", new List<string>());
            var raw = CollectMediaOnly(root, mediaTarget, info.ScanRecursive);
            var targets = FilterCaptionTargets(raw, mediaTarget, info.OverwriteSidecar, fmt);
            if (targets.Count == 0)
                return (false, info.OverwriteSidecar
                    ? (Localizer.Instance?["DatasetScanNoMedia"] ?? "扫描目录下没有符合当前打标对象的媒体文件")
                    : (Localizer.Instance?["DatasetScanNoPending"] ?? "扫描目录下没有待打标文件（已有打标文件且未勾选覆盖）"), new List<string>());
            return (true, "", targets);
        }

        List<string> paths;
        if (selectedOnly && selectedPaths != null && selectedPaths.Any())
        {
            paths = FilterCaptionTargets(selectedPaths, mediaTarget, true, fmt);
        }
        else
        {
            paths = FilterCaptionTargets(CollectMediaOnly(dir, mediaTarget, false), mediaTarget, info.OverwriteSidecar, fmt);
        }
        if (paths.Count == 0)
            return (false, selectedOnly
                ? (Localizer.Instance?["DatasetSelectedNoTarget"] ?? "选中的素材无法打标（不存在或不符合当前打标对象）")
                : (Localizer.Instance?["DatasetNoTarget"] ?? "没有可打标的素材"), new List<string>());
        return (true, "", paths);
    }

    /// <summary>启动批量打标：为每条媒体组装 ReverseCaptionRequest（train 工程映射 + 写侧车），复用反推服务。</summary>
    public async Task<IReadOnlyList<ReverseCaptionResult>> StartAutoCaptionAsync(
        string? name,
        IEnumerable<string> paths,
        ReverseCaptionRequest template,
        ProviderConfig? provider,
        ProviderModel? model,
        CancellationToken ct,
        IProgress<ReverseProgressEvent>? progress = null)
    {
        if (!TryGetDatasetDir(name, out var dir, out _)) return new List<ReverseCaptionResult>();
        var reqs = new List<ReverseCaptionRequest>();
        foreach (var p in paths)
        {
            var req = new ReverseCaptionRequest
            {
                MediaPath = p,
                MediaTarget = template.MediaTarget,
                CaptionLang = template.CaptionLang,
                CaptionModel = template.CaptionModel,
                Len = template.Len,
                CaptionLenChars = template.CaptionLenChars,
                ExtraPrompt = template.ExtraPrompt,
                CaptionFileFormat = template.CaptionFileFormat,
                Temperature = template.Temperature,
                TopP = template.TopP,
                ToriiUseNames = template.ToriiUseNames,
                ToriiAddTags = template.ToriiAddTags,
                ToriiGroundingTags = template.ToriiGroundingTags,
                ToriiGroundingCharacters = template.ToriiGroundingCharacters,
                WriteCaptionSidecar = true,
                WorkspaceDir = Path.GetFullPath(dir),
            };
            if (!string.IsNullOrEmpty(template.PeId))
                _peService.ApplyReverseToCaption(req, template.PeId);
            else if (!string.IsNullOrEmpty(template.Type))
                req.Type = template.Type;
            reqs.Add(req);
        }
        return await _reverse.CaptionBatchAsync(reqs, provider, model, ct, progress);
    }

    public void StopAutoCaption()
    {
        // 批量反推由上层 CancellationToken 控制（StopCommand 触发取消），无服务级长驻任务
    }
}
