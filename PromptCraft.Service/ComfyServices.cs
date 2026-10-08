using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PromptCraft.Service;

public class ImageSyncService : IImageSyncService
{
    private readonly ComfySettings _settings;
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IBaseLogService _log;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    };

    /// <summary>视频扩展名：同步扫描 + 读取容器元数据（工作流/提示词）+ 首帧抽封面，与图片共用一套缩略图/入库链路。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mkv", ".mov"
    };

    public ImageSyncService(ComfySettings settings, IDbContextFactory<ComfyDbContext> dbFactory, IBaseLogService log)
    {
        _settings = settings;
        _dbFactory = dbFactory;
        _log = log;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = new SyncResult();
        _log.Info(string.Format(Localizer.Instance?["SyncStartWithDir"] ?? "", _settings.ComfyOutputDir), "ImageSync");

        // 验证输出目录
        if (string.IsNullOrEmpty(_settings.ComfyOutputDir) || !Directory.Exists(_settings.ComfyOutputDir))
        {
            result.Errors++;
            result.ErrorMessages.Add(Localizer.Instance?["OutputDirMissing"] ?? "");
            _log.Warn(Localizer.Instance?["SyncAbortedNoOutputDir"] ?? "", "ImageSync");
            return result;
        }

        // 确保缩略图目录存在
        Directory.CreateDirectory(_settings.ThumbDir);

        // 扫描所有图片与视频文件（视频：读容器元数据 + 首帧抽封面，图库/菜单管理封面共用缩略图）
        var allFiles = Directory.GetFiles(_settings.ComfyOutputDir, "*.*", SearchOption.AllDirectories)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)) || VideoExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (allFiles.Count == 0)
        {
            result.NewFiles = 0;
            _log.Debug(Localizer.Instance?["NoImageFiles"] ?? "", "ImageSync");
            return result;
        }
        _log.Info(string.Format(Localizer.Instance?["ScannedFiles"] ?? "", allFiles.Count), "ImageSync");

        using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 确保数据库表已创建且 schema 已升级（老库手动补列/建表）
        await ComfyDbMigrator.EnsureSchemaAsync(db, ct);

        // 预加载去重字典（已有工作流/提示词），同步过程中复用 → 同一工作流/同一提示词只入库一次
        // 工作流按 ComfyUI 顶级 UUID（WorkflowGuid）去重；加载 ComfyUI 输出提取来源（image 图片 / video 视频）
        var workflowByGuid = new Dictionary<string, Workflow>(
            await db.Workflows.AsNoTracking()
                .Where(w => (w.Source == "image" || w.Source == "video") && w.WorkflowGuid != null)
                .ToDictionaryAsync(w => w.WorkflowGuid!, ct));
        var promptsByKey = new Dictionary<(int, string), ImagePrompt>(
            await db.ImagePrompts.AsNoTracking().ToDictionaryAsync(p => (p.WorkflowId, p.PromptHash), ct));
        var newPrompts = new Dictionary<(Workflow, string), ImagePrompt>();

        // 获取 DB 中已有的相对路径索引
        var existingPaths = new HashSet<string>(
            await db.ImageMetadata.Select(x => x.RelativePath).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

        // 内容级去重索引（Hash 唯一约束）：同一内容被复制/改名到不同路径时，
        // 相对路径不同但 Hash 相同 → 仅首次入库，其余跳过（图片与视频统一生效）
        var existingHashes = new HashSet<string>(
            await db.ImageMetadata.Select(x => x.Hash).ToListAsync(ct),
            StringComparer.Ordinal);

        foreach (var filePath in allFiles)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var relativePath = Path.GetRelativePath(_settings.ComfyOutputDir, filePath);
                var fileName = Path.GetFileName(filePath);
                var ext = Path.GetExtension(filePath);

                // 已存在则跳过
                if (existingPaths.Contains(relativePath))
                    continue;

                // 读取 ComfyUI 嵌入元数据：图片（PNG 文本块 / WebP EXIF）或视频（WebM Tags / MP4 ilst）。
                // 没有 prompt/workflow 元数据 → 判定为非 ComfyUI 生成，整文件跳过不入库。
                var isVideo = VideoExtensions.Contains(ext);
                var metadata = isVideo
                    ? ComfyVideoMetadataReader.TryRead(filePath)
                    : ComfyImageMetadataReader.TryRead(filePath);
                if (metadata == null)
                {
                    result.SkippedFiles++;
                    _log.Debug(string.Format(Localizer.Instance?["SkipNonComfyImage"] ?? "", filePath), "ImageSync");
                    continue;
                }

                // 新版 ComfyUI 工作流要求顶级 UUID（"id": guid）做唯一键；
                // 无顶级 id（旧格式/异常）→ 不支持，整张图跳过不入库。
                if (!TryGetWorkflowGuid(metadata.Workflow, out _))
                {
                    result.SkippedFiles++;
                    _log.Debug(string.Format(Localizer.Instance?["SkipNoWorkflowId"] ?? "", filePath), "ImageSync");
                    continue;
                }

                // 工作流必须含有效节点（nodes 非空数组）；空工作流视为无效 → 整张图跳过不入库。
                if (!HasWorkflowNodes(metadata.Workflow))
                {
                    result.SkippedFiles++;
                    _log.Debug(string.Format(Localizer.Instance?["SkipEmptyWorkflow"] ?? "", filePath), "ImageSync");
                    continue;
                }

                // 读取文件尺寸：图片用 SkiaSharp；视频由 GenerateVideoThumbnailAsync 抓首帧时回传（一次解码）
                int width = 0, height = 0;
                if (!isVideo)
                {
                    try
                    {
                        using var input = File.OpenRead(filePath);
                        using var codec = SKCodec.Create(input);
                        if (codec != null)
                        {
                            width = codec.Info.Width;
                            height = codec.Info.Height;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 无法读取尺寸，可能文件损坏
                        _log.Debug(string.Format(Localizer.Instance?["ReadSizeFailed"] ?? "", filePath), "ImageSync", ex);
                    }
                }

                // 宽高优先采用图片实际尺寸（EmptyLatentImage 等节点值已由提取器填入，这里兜底）
                if (metadata.Extracted != null)
                {
                    metadata.Extracted.Width ??= width;
                    metadata.Extracted.Height ??= height;
                }

                // 生成缩略图（文件名含相对路径哈希，不同目录同名文件不撞车）。
                // 视频走 LibVLC 抓首帧（首帧即封面），同时回传画面尺寸（一次解码，避免二次解析轨道）。
                string thumbPath;
                if (isVideo)
                {
                    (thumbPath, width, height) = await GenerateVideoThumbnailAsync(filePath, relativePath, ct);
                }
                else
                {
                    thumbPath = await GenerateThumbnailAsync(filePath, relativePath, ct);
                }

                // 计算文件哈希
                var hash = await ComputeHashAsync(filePath, ct);

                // 内容级去重：Hash 已在库（同内容不同路径的复制/改名场景）→ 跳过，避免 UNIQUE 约束冲突
                if (!string.IsNullOrEmpty(hash) && existingHashes.Contains(hash))
                {
                    result.SkippedFiles++;
                    _log.Debug(string.Format(Localizer.Instance?["SkipDuplicateHash"] ?? "跳过重复内容文件: {0}", filePath), "ImageSync");
                    continue;
                }
                if (!string.IsNullOrEmpty(hash))
                    existingHashes.Add(hash); // 本轮扫描内后续同 Hash 文件同样跳过

                var fileInfo = new FileInfo(filePath);

                // 写入 DB：工作流按顶级 id 去重复用，图片关联其 Id（压缩存储，按需解压）
                var (workflow, prompt) = await AttachMetadataAsync(db, metadata, ct, workflowByGuid, promptsByKey, newPrompts, isVideo, thumbPath);
                db.ImageMetadata.Add(new ImageInfo
                {
                    RelativePath = relativePath,
                    FileName = fileName,
                    Extension = ext,
                    FileSize = fileInfo.Length,
                    Width = width,
                    Height = height,
                    Hash = hash,
                    CreatedAt = fileInfo.LastWriteTimeUtc,
                    ImportedAt = DateTime.UtcNow,
                    Workflow = workflow,
                    Prompt = prompt,
                    Status = new ImageStatus()
                });
                result.NewFiles++;
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorMessages.Add(string.Format(Localizer.Instance?["ImageProcessingError"] ?? "", filePath, ex.Message));
                _log.Warn(string.Format(Localizer.Instance?["ProcessImageFailed"] ?? "", filePath), "ImageSync", ex);
            }
        }

        // 批量写入
        if (result.NewFiles > 0)
            await db.SaveChangesAsync(ct);

        // 存量回填：历史同步的图片（MetadataBlob 为空）重读原文件补全元数据
        await BackfillMissingMetadataAsync(db, result, ct);

        // 缩略图迁移：旧命名缩略图（纯文件名）补齐新命名（相对路径哈希），同名文件不再互相覆盖
        await MigrateThumbnailsAsync(db, result, ct);

        _log.Info(string.Format(Localizer.Instance?["ImageSyncDone"] ?? "", result.NewFiles, result.UpdatedFiles, result.DeletedFiles, result.SkippedFiles, result.Errors), "ImageSync");
        return result;
    }

    /// <summary>
    /// 缩略图命名迁移：为已有图片补齐"相对路径哈希"命名的新缩略图。
    /// 旧版缩略图按纯文件名命名，不同目录同名文件会互相覆盖；新命名唯一，
    /// 迁移后画廊读取侧不再回退到旧文件，同名撞车彻底消除。
    /// </summary>
    private async Task MigrateThumbnailsAsync(ComfyDbContext db, SyncResult result, CancellationToken ct)
    {
        var images = await db.ImageMetadata.AsNoTracking().ToListAsync(ct);
        foreach (var img in images)
        {
            if (ct.IsCancellationRequested) break;

            // 新命名已存在则跳过（幂等，只需迁移一次）
            var thumbPath = Path.Combine(_settings.ThumbDir,
                GetThumbFileName(img.RelativePath, _settings.ThumbMaxDimension));
            if (File.Exists(thumbPath)) continue;

            var fullPath = Path.Combine(_settings.ComfyOutputDir, img.RelativePath);
            if (!File.Exists(fullPath)) continue;

            await GenerateThumbnailAsync(fullPath, img.RelativePath, ct);
            result.UpdatedFiles++;
        }
        if (result.UpdatedFiles > 0)
            _log.Debug(string.Format(Localizer.Instance?["ThumbMigrateDone"] ?? "", result.UpdatedFiles), "ImageSync");
    }

    /// <summary>
    /// 存量回填：为尚未关联工作流/提示词的 PNG/WebP 记录补齐元数据。
    /// 两类来源：①上一版存入 MetadataBlob 的旧压缩文档（反序列化后拆表）；②从未读过的原文件（重读提取）。
    /// 仍读不到元数据的记录保持原样（可能是历史遗留的非 ComfyUI 图，保守不删除）。
    /// 成功回填的数量计入 <see cref="SyncResult.UpdatedFiles"/>。
    /// </summary>
    private async Task BackfillMissingMetadataAsync(ComfyDbContext db, SyncResult result, CancellationToken ct)
    {
        // 仅回填当前读取器支持的格式；WorkflowId/PromptId 均空才需要处理（含上一版 MetadataBlob 旧数据）
        var missing = await db.ImageMetadata
            .Where(x => x.WorkflowId == null && x.PromptId == null && (x.Extension == ".png" || x.Extension == ".webp"))
            .ToListAsync(ct);
        if (missing.Count == 0) return;

        // 上一版 MetadataBlob 是物理列但已从 EF 模型移除，用原始 SQL 读取旧压缩文档做迁移
        var legacyDocs = new Dictionary<int, ImageMetadataDocument>();
        {
            var conn = db.Database.GetDbConnection();
            bool needClose = conn.State != System.Data.ConnectionState.Open;
            if (needClose) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT \"Id\", \"MetadataBlob\" FROM \"ImageMetadata\" WHERE \"MetadataBlob\" IS NOT NULL";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    int id = reader.GetInt32(0);
                    byte[]? blob = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
                    if (blob is { Length: > 0 } && ComfyMetadataCodec.Deserialize(blob) is { } doc)
                        legacyDocs[id] = doc;
                }
            }
            finally
            {
                if (needClose) await conn.CloseAsync();
            }
        }

        var workflowByGuid = new Dictionary<string, Workflow>(
            await db.Workflows.AsNoTracking()
                .Where(w => (w.Source == "image" || w.Source == "video") && w.WorkflowGuid != null)
                .ToDictionaryAsync(w => w.WorkflowGuid!, ct));
        var promptsByKey = new Dictionary<(int, string), ImagePrompt>(
            await db.ImagePrompts.AsNoTracking().ToDictionaryAsync(p => (p.WorkflowId, p.PromptHash), ct));
        var newPrompts = new Dictionary<(Workflow, string), ImagePrompt>();

        foreach (var img in missing)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // ① 上一版压缩文档（MetadataBlob）直接反序列化，避免重读文件
                ImageMetadataDocument? doc = legacyDocs.TryGetValue(img.Id, out var legacy) ? legacy : null;

                // ② 否则重读原文件
                if (doc == null)
                {
                    var fullPath = Path.Combine(_settings.ComfyOutputDir, img.RelativePath);
                    if (!File.Exists(fullPath)) continue;
                    doc = ComfyImageMetadataReader.TryRead(fullPath);
                }
                if (doc == null) continue;

                // 无工作流顶级 id（旧格式/异常）→ 不支持，保持无关联（与同步时"跳过整图"同级语义）
                if (!TryGetWorkflowGuid(doc.Workflow, out _)) continue;

                // 空工作流（无有效节点）→ 同样视为无效，保持无关联
                if (!HasWorkflowNodes(doc.Workflow)) continue;

                var (workflow, prompt) = await AttachMetadataAsync(db, doc, ct, workflowByGuid, promptsByKey, newPrompts, isVideo: false, thumbPath: null);
                img.Workflow = workflow;
                img.Prompt = prompt;
                result.UpdatedFiles++;
            }
            catch (Exception ex)
            {
                _log.Debug(string.Format(Localizer.Instance?["BackfillMetadataFailed"] ?? "", img.RelativePath), "ImageSync", ex);
            }
        }

        if (result.UpdatedFiles > 0)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 提取工作流顶级 UUID（新版 ComfyUI 格式 "id": "xxxx-xxxx"）。
    /// 无顶级 id（旧格式/异常）返回 false —— 该图不被支持，整图跳过。
    /// </summary>
    private static bool TryGetWorkflowGuid(JsonElement? workflow, out string guid)
    {
        guid = "";
        if (workflow is not JsonElement wf || wf.ValueKind != JsonValueKind.Object) return false;
        if (!wf.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
        guid = idEl.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(guid);
    }

    /// <summary>
    /// 工作流是否含有效节点（顶级 "nodes" 是非空数组）。
    /// 空工作流（仅有顶级 id、没有画布节点）视为无效，整图跳过不入库。
    /// </summary>
    private static bool HasWorkflowNodes(JsonElement? workflow)
    {
        if (workflow is not JsonElement wf || wf.ValueKind != JsonValueKind.Object) return false;
        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return false;
        return nodes.GetArrayLength() > 0;
    }

    /// <summary>
    /// 把提取出的元数据写入工作流/提示词表（按工作流顶级 id 去重复用），返回关联实体。
    /// 工作流：WorkflowGuid（ComfyUI 顶级 UUID）唯一 → 已存在则复用，不重复添加；
    ///         无顶级 id 的工作流不支持 → 返回 (null, null)，由调用方跳过整图。
    /// 提示词：同一工作流内 PromptHash 唯一 → 已存在则复用。
    /// 工作流只存"结构版" JSON（参数/提示词已剔除，GZip 压缩），
    /// 详情/对比展示时用该图片的参数（提示词表 NodesJsonBlob）组装回完整工作流。
    /// 新建实体仅 Add 到 ChangeTracker，由调用方统一 SaveChanges 后 EF 填充自增 Id，
    /// 因此图片直接用对象引用（<c>image.Workflow / image.Prompt</c>）关联即可。
    /// </summary>
    private static async Task<(Workflow? Workflow, ImagePrompt? Prompt)> AttachMetadataAsync(
        ComfyDbContext db,
        ImageMetadataDocument metadata,
        CancellationToken ct,
        Dictionary<string, Workflow> workflowByGuid,
        Dictionary<(int, string), ImagePrompt> promptsByKey,
        Dictionary<(Workflow, string), ImagePrompt> newPrompts,
        bool isVideo = false,
        string? thumbPath = null)
    {
        // ---- 工作流：按 ComfyUI 顶级 UUID 去重（无 id / 空工作流不支持 → 整图跳过）----
        Workflow? workflow = null;
        if (TryGetWorkflowGuid(metadata.Workflow, out var wfGuid)
            && HasWorkflowNodes(metadata.Workflow))
        {
            var structureJson = ComfyWorkflowNormalizer.ToStructure(metadata.Workflow!.Value);
            if (workflowByGuid.TryGetValue(wfGuid, out var existingWf))
            {
                workflow = existingWf;
                // 复用 DB 已有工作流：显式 Attach 为 Unchanged，防止调用方 Add(ImageInfo) 的
                // graph 遍历把它连带标记为 Added（→ 按原 Id 重复插入 → UNIQUE constraint failed: Workflows.Id）。
                if (db.Entry(existingWf).State == EntityState.Detached)
                {
                    // context 可能已跟踪同 Id 的另一实例（本批先创建、或先前已 Attach 过）：
                    // 直接复用该跟踪实例，避免 Attach 双实例冲突（InvalidOperationException）；没有则 Attach 当前实例。
                    var tracked = db.Workflows.Local.FirstOrDefault(w => w.Id == existingWf.Id);
                    if (tracked != null)
                    {
                        workflow = tracked;
                        workflowByGuid[wfGuid] = tracked;
                    }
                    else
                    {
                        db.Workflows.Attach(existingWf);
                    }
                }
            }
            else
            {
                var title = ComfyMetadataExtractor.ExtractTitle(metadata.Workflow.Value)
                            ?? metadata.Extracted?.Nodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.Title))?.Title;
                workflow = new Workflow
                {
                    Name = title ?? wfGuid[..8],
                    // 来源区分：图片提取 "image" / 视频提取 "video"（ComfyUI 输出统一按 WorkflowGuid 去重）
                    Source = isVideo ? "video" : "image",
                    WorkflowGuid = wfGuid,
                    // 视频提取的工作流保存首帧封面路径，工作流列表/详情直接加载该 jpg；
                    // 图片保持 null（由读侧按关联第一张图兜底）
                    ThumbnailPath = thumbPath,
                    // 只存结构（widgets_values/widgets_values_named/models 已剔除），参数/提示词由提示词表按图片保存
                    WorkflowJsonBlob = ComfyMetadataCodec.Compress(structureJson),
                    NodeCount = metadata.Extracted?.Nodes.Count ?? 0,
                };
                workflowByGuid[wfGuid] = workflow;
                db.Workflows.Add(workflow);
            }
        }
        else
        {
            // 无顶级 id 或空工作流（无有效节点）：不支持，返回 null 由调用方跳过整图
            return (null, null);
        }

        // ---- 提示词：同一工作流内按 PromptHash 去重 ----
        ImagePrompt? prompt = null;
        if (metadata.Prompt is JsonElement p && p.ValueKind == JsonValueKind.Object && workflow != null)
        {
            var pHash = ComfyMetadataCodec.ComputeJsonHash(p.GetRawText());

            // DB 已有的工作流（Id>0）查 DB 字典；本批新建的按对象引用查本地字典
            ImagePrompt? existingP = workflow.Id > 0 && promptsByKey.TryGetValue((workflow.Id, pHash), out var ep)
                ? ep
                : newPrompts.TryGetValue((workflow, pHash), out var np)
                    ? np
                    : null;
            if (existingP != null)
            {
                prompt = existingP;
                // 复用 DB 已有提示词：显式 Attach 为 Unchanged（同上，防止被 ImageInfo.Add 连带标记 Added → 按原 Id 重复插入冲突）
                if (db.Entry(existingP).State == EntityState.Detached)
                {
                    var trackedP = db.ImagePrompts.Local.FirstOrDefault(p => p.Id == existingP.Id);
                    if (trackedP != null)
                        prompt = trackedP;
                    else
                        db.ImagePrompts.Attach(existingP);
                }
                // 老数据可能因提取规则升级而缺提示词/参数（如 Qwen 的 prompt/negative_prompt 字段），用本次提取结果补全缺失字段
                var exOld = metadata.Extracted;
                if (exOld != null)
                {
                    prompt.PositivePrompt ??= exOld.PositivePrompt;
                    prompt.NegativePrompt ??= exOld.NegativePrompt;
                    prompt.Model ??= exOld.Checkpoint;
                    prompt.Seed ??= exOld.Seed;
                    prompt.Steps ??= exOld.Steps;
                    prompt.Cfg ??= exOld.Cfg;
                    prompt.Sampler ??= exOld.Sampler;
                    prompt.Scheduler ??= exOld.Scheduler;
                    prompt.Width ??= exOld.Width;
                    prompt.Height ??= exOld.Height;
                    if (exOld is { LoraNames.Count: > 0 } && string.IsNullOrEmpty(prompt.LoraNames))
                        prompt.LoraNames = string.Join(", ", exOld.LoraNames);
                    if (prompt.NodesJsonBlob == null && exOld is { Nodes.Count: > 0 })
                        prompt.NodesJsonBlob = ComfyMetadataCodec.Compress(JsonSerializer.Serialize(exOld.Nodes));
                }
            }
            else
            {
                var ex = metadata.Extracted;
                prompt = new ImagePrompt
                {
                    Workflow = workflow,
                    PromptHash = pHash,
                    PromptJsonBlob = ComfyMetadataCodec.Compress(p.GetRawText()),
                    PositivePrompt = ex?.PositivePrompt,
                    NegativePrompt = ex?.NegativePrompt,
                    Model = ex?.Checkpoint,
                    Seed = ex?.Seed,
                    Steps = ex?.Steps,
                    Cfg = ex?.Cfg,
                    Sampler = ex?.Sampler,
                    Scheduler = ex?.Scheduler,
                    Width = ex?.Width,
                    Height = ex?.Height,
                    LoraNames = ex is { LoraNames.Count: > 0 } ? string.Join(", ", ex.LoraNames) : null,
                    NodesJsonBlob = ex is { Nodes.Count: > 0 }
                        ? ComfyMetadataCodec.Compress(JsonSerializer.Serialize(ex.Nodes))
                        : null,
                };
                newPrompts[(workflow, pHash)] = prompt;
                db.ImagePrompts.Add(prompt);
            }
        }

        return (workflow, prompt);
    }

    /// <summary>
    /// 缩略图文件名：相对路径 SHA256 前 16 位 + 尺寸后缀。
    /// 不同目录下的同名文件相对路径不同 → 哈希不同 → 缩略图互不覆盖。
    /// 画廊读取侧必须用同一规则（<see cref="ComfyGalleryModel.GetThumbnailPath"/>）。
    /// </summary>
    public static string GetThumbFileName(string relativePath, int maxDimension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));
        return $"{hash[..16].ToLowerInvariant()}_{maxDimension}.jpg";
    }

    /// <summary>生成缩略图，返回缩略图路径（文件名由相对路径哈希决定）。
    /// public 供执行自动入库（ComfyUIService）等场景复用，与图库同步共用同一套生成规则。
    /// 图片走 SkiaSharp 解码缩放；视频走 LibVLC 抓首帧 → Skia 缩放（首帧即封面，图库/菜单管理共用）。</summary>
    public async Task<string> GenerateThumbnailAsync(string sourcePath, string relativePath, CancellationToken ct)
    {
        var thumbFileName = GetThumbFileName(relativePath, _settings.ThumbMaxDimension);
        var thumbPath = Path.Combine(_settings.ThumbDir, thumbFileName);
        await Task.Run(() =>
        {
            try
            {
                // 图片必须走 Skia 原生解码管线（SKImage.FromEncodedData → 缩放 → JPEG）；
                // 不可委托视频抓帧管线——LibVLC 把图片当"一帧视频"渲染，色彩/伽马经转换，画质明显劣化
                GenerateImageThumbCore(sourcePath, thumbPath);
            }
            catch (Exception ex)
            {
                // 缩略图生成失败不阻塞同步
                _log.Debug(string.Format(Localizer.Instance?["GenThumbnailFailed"] ?? "", sourcePath), "ImageSync", ex);
            }
        }, ct);
        return thumbPath;
    }

    /// <summary>视频缩略图：LibVLC 抓首帧 → Skia 等比缩放 → JPEG。
    /// 命中缓存不再重生成，但每次仍抓一次帧回传画面尺寸（供 ImageInfo.Width/Height 入库，一次解码）。
    /// 图片调用方请使用 <see cref="GenerateThumbnailAsync"/>。</summary>
    public async Task<(string ThumbPath, int Width, int Height)> GenerateVideoThumbnailAsync(string sourcePath, string relativePath, CancellationToken ct)
    {
        var thumbFileName = GetThumbFileName(relativePath, _settings.ThumbMaxDimension);
        var thumbPath = Path.Combine(_settings.ThumbDir, thumbFileName);

        int width = 0, height = 0;
        await Task.Run(() =>
        {
            try
            {
                (width, height) = GenerateVideoThumbCore(sourcePath, thumbPath);
            }
            catch (Exception ex)
            {
                // 缩略图生成失败不阻塞同步
                _log.Debug(string.Format(Localizer.Instance?["GenThumbnailFailed"] ?? "", sourcePath), "ImageSync", ex);
            }
        }, ct);

        return (thumbPath, width, height);
    }

    /// <summary>图片缩略图：SkiaSharp 解码 → 等比缩放 → JPEG。</summary>
    private void GenerateImageThumbCore(string sourcePath, string thumbPath)
    {
        using var input = File.OpenRead(sourcePath);
        using var original = SKImage.FromEncodedData(input);
        if (original == null) return;

        float scale = Math.Min(
            (float)_settings.ThumbMaxDimension / original.Width,
            (float)_settings.ThumbMaxDimension / original.Height);
        if (scale >= 1f) scale = 1f;

        int newWidth = (int)(original.Width * scale);
        int newHeight = (int)(original.Height * scale);
        if (newWidth < 1) newWidth = 1;
        if (newHeight < 1) newHeight = 1;

        var info = new SKImageInfo(newWidth, newHeight);
        using var surface = SKSurface.Create(info);
        if (surface == null) return;
        surface.Canvas.DrawImage(original, new SKRect(0, 0, newWidth, newHeight));
        using var resized = surface.Snapshot();
        using var data = resized.Encode(SKEncodedImageFormat.Jpeg, _settings.ThumbQuality);
        using var output = File.OpenWrite(thumbPath);
        data.SaveTo(output);
    }

    /// <summary>视频缩略图核心：抓首帧像素（BGRA8888）→ Skia 等比缩放 → JPEG；返回 (宽, 高)。
    /// 命中缓存直接返回 (0,0)（不再重生成）。仅生成一次，图库/菜单管理封面读取侧与图片完全同规则。</summary>
    private (int Width, int Height) GenerateVideoThumbCore(string sourcePath, string thumbPath)
    {
        if (File.Exists(thumbPath))
            return (0, 0);

        var frame = VideoFrameGrab.GrabFirstFrameData(VideoLibVlc, sourcePath);
        if (frame?.Data == null || frame.Width <= 0 || frame.Height <= 0) return (0, 0);

        float scale = Math.Min(
            (float)_settings.ThumbMaxDimension / frame.Width,
            (float)_settings.ThumbMaxDimension / frame.Height);
        if (scale >= 1f) scale = 1f;

        int newWidth = (int)(frame.Width * scale);
        int newHeight = (int)(frame.Height * scale);
        if (newWidth < 1) newWidth = 1;
        if (newHeight < 1) newHeight = 1;

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(frame.Data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var src = SKImage.FromPixels(
                new SKImageInfo((int)frame.Width, (int)frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                handle.AddrOfPinnedObject(),
                (int)frame.Width * 4);

            var info = new SKImageInfo(newWidth, newHeight);
            using var surface = SKSurface.Create(info);
            if (surface == null) return (0, 0);
            surface.Canvas.DrawImage(src, new SKRect(0, 0, newWidth, newHeight));
            using var resized = surface.Snapshot();
            using var data = resized.Encode(SKEncodedImageFormat.Jpeg, _settings.ThumbQuality);
            using var output = File.OpenWrite(thumbPath);
            data.SaveTo(output);
        }
        finally
        {
            handle.Free();
        }
        return ((int)frame.Width, (int)frame.Height);
    }

    /// <summary>服务层共享 LibVLC 实例（抓视频首帧用；同步/迁移低频操作，懒加载常驻进程）。</summary>
    private static LibVLCSharp.Shared.LibVLC? _videoLibVlc;
    private static LibVLCSharp.Shared.LibVLC VideoLibVlc =>
        _videoLibVlc ??= new LibVLCSharp.Shared.LibVLC(enableDebugLogs: false, "--avcodec-hw=any", "--file-caching=300");

    /// <summary>计算文件 SHA256 哈希</summary>
    private async Task<string?> ComputeHashAsync(string filePath, CancellationToken ct)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hashBytes = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexStringLower(hashBytes);
        }
        catch (Exception ex)
        {
            _log.Debug(string.Format(Localizer.Instance?["CalcHashFailed"] ?? "", filePath), "ImageSync", ex);
            return null;
        }
    }

    /// <summary>
    /// 删除图片（单张/批量共用）：①删除原文件 + 缩略图（相对路径哈希命名 + 兼容旧纯文件名命名）；
    /// ②删除入库记录（级联 ImageStatus/ImageTags；JobOutput 外键已配置 SetNull）；
    /// ③清理不再被任何图片引用的提示词（ImagePrompt，同一工作流内多图共享时保留，保证外键完整）。
    /// 文件删除失败（如被占用）的图片跳过数据库删除，避免"记录删了文件还在 → 下次同步又入库"的不一致。
    /// </summary>
    public async Task<ImageDeleteResult> DeleteImagesAsync(IReadOnlyCollection<int> imageIds, CancellationToken ct = default)
    {
        var result = new ImageDeleteResult();
        if (imageIds == null || imageIds.Count == 0) return result;

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var images = await db.ImageMetadata
            .Include(i => i.Status)
            .Include(i => i.ImageTags)
            .Where(i => imageIds.Contains(i.Id))
            .ToListAsync(ct);
        if (images.Count == 0) return result;

        // 本批待删图片 Id（提示词引用计数时排除，批量删除多图共享同一提示词不会误判为仍被引用）
        var pendingIds = images.Select(i => i.Id).ToHashSet();
        var promptIds = images.Where(i => i.PromptId != null).Select(i => i.PromptId!.Value).Distinct().ToList();

        // ① 物理文件：原图 + 新命名缩略图 + 旧命名缩略图；任一失败则该图跳过数据库删除
        var toDelete = new List<ImageInfo>();
        foreach (var img in images)
        {
            var errors = new List<string>();
            TryDeleteFile(Path.Combine(_settings.ComfyOutputDir, img.RelativePath), errors);
            TryDeleteFile(Path.Combine(_settings.ThumbDir, GetThumbFileName(img.RelativePath, _settings.ThumbMaxDimension)), errors);
            TryDeleteFile(Path.Combine(_settings.ThumbDir, $"{Path.GetFileNameWithoutExtension(img.FileName)}_{_settings.ThumbMaxDimension}.jpg"), errors);

            if (errors.Count > 0)
            {
                result.Failed++;
                result.Errors.AddRange(errors);
                _log.Warn(string.Format(Localizer.Instance?["DeleteImageFilePartialFailed"] ?? "", string.Join("; ", errors)), "ImageSync");
                continue;
            }
            toDelete.Add(img);
        }

        // ② 入库记录（EF 级联删除 ImageStatus/ImageTags）
        if (toDelete.Count > 0)
        {
            db.ImageMetadata.RemoveRange(toDelete);

            // ③ 清理不再被任何图片引用的提示词（排除本批删除的图片）
            foreach (var promptId in promptIds)
            {
                var stillUsed = await db.ImageMetadata.AsNoTracking()
                    .AnyAsync(i => i.PromptId == promptId && !pendingIds.Contains(i.Id), ct);
                if (stillUsed) continue;

                var prompt = await db.ImagePrompts.FirstOrDefaultAsync(p => p.Id == promptId, ct);
                if (prompt != null)
                    db.ImagePrompts.Remove(prompt);
            }

            await db.SaveChangesAsync(ct);
            result.Deleted = toDelete.Count;
            _log.Info(string.Format(Localizer.Instance?["DeleteImagesDone"] ?? "", result.Deleted), "ImageSync");
        }
        return result;
    }

    /// <summary>删除单个文件；不存在视为成功。失败时把原因写入 errors。</summary>
    private static void TryDeleteFile(string path, List<string> errors)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add(string.Format(Localizer.Instance?["FileErrorEntry"] ?? "", path, ex.Message));
        }
    }
}

public class ComfyUIClient : IComfyUIClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IBaseLogService _log;

    public ComfyUIClient(ComfySettings settings, IBaseLogService? log = null)
    {
        _baseUrl = settings.ComfyApiUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // 直接 new 的场景（如设置页连接测试）未传日志时使用静态引导实例
        _log = log ?? PromptCraft.Service.LogService.Instance;
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/queue");
            _log.Debug(string.Format(Localizer.Instance?["ComfyTestResult"] ?? "", _baseUrl, resp.IsSuccessStatusCode ? Localizer.Instance?["Success"] ?? "" : "HTTP " + (int)resp.StatusCode), "ComfyUI");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["ComfyTestFailed"] ?? "", _baseUrl), "ComfyUI", ex);
            return false;
        }
    }

    #region 队列状态查询

    public async Task<QueueStatusResponse?> GetQueueStatusAsync()
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/queue");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<QueueStatusResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["GetQueueFailed"] ?? "", _baseUrl), "ComfyUI", ex);
            return null;
        }
    }

    #endregion

    #region 节点信息获取

    /// <summary>
    /// 获取所有节点定义（GET /object_info）。
    /// 供 UI→API 工作流转换器使用：每个节点类型的 input.required/optional 决定 widget 输入映射。
    /// </summary>
    public async Task<ObjectInfoResponse?> GetObjectInfoAsync()
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/object_info");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var result = new ObjectInfoResponse();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result.Nodes[prop.Name] = NodeObjectInfo.Parse(prop.Name, prop.Value);
            }
            _log.Debug(string.Format(Localizer.Instance?["NodeInfoFetched"] ?? "", result.Nodes.Count), "ComfyUI");
            return result;
        }
        catch (Exception ex)
        {
            _log.Warn(Localizer.Instance?["NodeInfoFetchFailed"] ?? "", "ComfyUI", ex);
            return null;
        }
    }

    #endregion

    #region 用户数据（工作流文件）管理

    /// <summary>
    /// 列出用户工作流目录下的文件。
    /// 最新版 ComfyUI 端点：GET /api/userdata?dir=workflows&recurse=true&split=false&full_info=true
    /// 返回数组项：{ path, size, modified, created }，path 为相对 dir 的路径，modified/created 为 Unix 毫秒时间戳。
    /// </summary>
    public async Task<List<UserDataFileInfo>> ListUserDataWorkflowsAsync()
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/userdata?dir=workflows&recurse=true&split=false&full_info=true");
            if (!response.IsSuccessStatusCode) return new List<UserDataFileInfo>();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var result = new List<UserDataFileInfo>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var path = item.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString() : null;
                    if (string.IsNullOrEmpty(path)) continue;
                    result.Add(new UserDataFileInfo
                    {
                        Name = path,
                        Size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
                        MTime = item.TryGetProperty("modified", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 0,
                    });
                }
            }
            _log.Debug(string.Format(Localizer.Instance?["ListWorkflowsDone"] ?? "", result.Count), "ComfyUI");
            return result;
        }
        catch (Exception ex)
        {
            _log.Warn(Localizer.Instance?["ListWorkflowsFailed"] ?? "", "ComfyUI", ex);
            return new List<UserDataFileInfo>();
        }
    }

    /// <summary>读取用户数据文件内容（GET /api/userdata/workflows/{file}）。file 为相对 workflows 目录的路径。</summary>
    public async Task<string?> GetUserDataFileAsync(string file)
    {
        try
        {
            var url = BuildUserDataFileUrl(file);
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["ReadUserFileFailed"] ?? "", file), "ComfyUI", ex);
            return null;
        }
    }

    /// <summary>保存用户数据文件（POST /api/userdata/workflows/{file}），用于把工作流写回 ComfyUI。</summary>
    public async Task<bool> SaveUserDataFileAsync(string file, string content)
    {
        try
        {
            var body = new StringContent(content, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(BuildUserDataFileUrl(file), body);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn(string.Format(Localizer.Instance?["SaveUserFileFailed"] ?? "", file, (int)response.StatusCode), "ComfyUI");
                return false;
            }
            _log.Info(string.Format(Localizer.Instance?["SaveUserFileDone"] ?? "", file), "ComfyUI");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["SaveUserFileException"] ?? "", file), "ComfyUI", ex);
            return false;
        }
    }

    /// <summary>删除用户数据文件（DELETE /api/userdata/workflows/{file}），用于清理 ComfyUI 侧已保存的工作流。</summary>
    public async Task<bool> DeleteUserDataFileAsync(string file)
    {
        try
        {
            var response = await _http.DeleteAsync(BuildUserDataFileUrl(file));
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn(string.Format(Localizer.Instance?["DeleteUserFileFailed"] ?? "", file, (int)response.StatusCode), "ComfyUI");
                return false;
            }
            _log.Info(string.Format(Localizer.Instance?["DeleteUserFileDone"] ?? "", file), "ComfyUI");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["DeleteUserFileException"] ?? "", file), "ComfyUI", ex);
            return false;
        }
    }

    /// <summary>构造 /api/userdata/workflows/{file} URL（file 已含 workflows/ 前缀则直接用）。</summary>
    private string BuildUserDataFileUrl(string file)
    {
        var relative = file.StartsWith("workflows/", StringComparison.OrdinalIgnoreCase)
            ? file
            : "workflows/" + file;
        return $"{_baseUrl}/api/userdata/{Uri.EscapeDataString(relative)}";
    }

    #endregion

    #region 提交 Workflow

    /// <summary>
    /// 提交工作流到 ComfyUI 队列（最新版端点 POST /api/prompt）。
    /// 携带自建 client_id 用于 WebSocket 订阅；prompt_id 由服务端生成，从响应中读取用于追踪执行结果。
    /// extra_data 携带 comfy_usage_source 与 extra_pnginfo.workflow（UI 格式），
    /// 使本次执行生成的 PNG 携带 workflow 元数据，可被图片提取链路复用。
    /// </summary>
    public async Task<SubmitWorkflowResponse> SubmitWorkflowAsync(string promptJson, string clientId, string? uiWorkflowJson = null)
    {
        try
        {
            var extraData = new Dictionary<string, object>
            {
                ["comfy_usage_source"] = "comfyui-frontend",
            };
            if (!string.IsNullOrEmpty(uiWorkflowJson))
            {
                extraData["extra_pnginfo"] = new Dictionary<string, object>
                {
                    ["workflow"] = JsonSerializer.Deserialize<JsonElement>(uiWorkflowJson),
                };
            }

            var payload = new Dictionary<string, object>
            {
                ["client_id"] = clientId,
                ["extra_data"] = extraData,
                ["prompt"] = JsonSerializer.Deserialize<JsonElement>(promptJson),
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{_baseUrl}/api/prompt", content);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // 客户端层只记状态码；完整响应由服务层（SubmitCoreAsync）统一以 Error 记录，避免同一次失败产生多条重复日志
                _log.Warn(string.Format(Localizer.Instance?["SubmitWorkflowHttpFailed"] ?? "", (int)response.StatusCode), "ComfyUI");
                return new SubmitWorkflowResponse { Success = false, ErrorMessage = json };
            }
            var result = JsonSerializer.Deserialize<SubmitWorkflowJsonResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (string.IsNullOrEmpty(result?.PromptId))
            {
                _log.Warn(string.Format(Localizer.Instance?["SubmitMissingPromptId"] ?? "", json), "ComfyUI");
                return new SubmitWorkflowResponse { Success = false, ErrorMessage = Localizer.Instance?["MissingPromptIdMsg"] ?? "" };
            }
            _log.Info(string.Format(Localizer.Instance?["SubmitSuccess"] ?? "", result.PromptId), "ComfyUI");
            return new SubmitWorkflowResponse
            {
                Success = true,
                PromptId = result.PromptId
            };
        }
        catch (Exception ex)
        {
            _log.Warn(Localizer.Instance?["SubmitException"] ?? "", "ComfyUI", ex);
            return new SubmitWorkflowResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>查询指定 prompt 的执行历史（GET /history/{prompt_id}），完成后可从中取输出图片。</summary>
    public async Task<HistoryDetailResponse?> GetHistoryAsync(string promptId)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/history/{Uri.EscapeDataString(promptId)}");
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            // 返回 { prompt_id: { prompt, outputs, status } }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                return HistoryDetailResponse.Parse(prop.Value);
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["GetHistoryFailed"] ?? "", promptId), "ComfyUI", ex);
            return null;
        }
    }

    #endregion
}

/// <summary>
/// UI 格式工作流 → API 格式转换器。
/// ComfyUI 的 /prompt 只接受 API 格式（{节点id: {class_type, inputs}}），
/// 而数据库/图片元数据里以 UI 格式（{nodes, links}）为主，提交前必须转换。
/// 转换算法与 ComfyUI 前端 convertWorkflowToAPI 一致：
/// 1) 按节点类型查 /object_info 的 input.required/optional 定义字段顺序；
/// 2) widgets_values 按序映射到非连线（type 不以 * 开头）字段；
/// 3) node.inputs 的 link 按 links 表解析为 [源节点id, 源输出槽]。
/// </summary>
public static class WorkflowJsonConverter
{
    /// <summary>判定输入是否已是 API 格式：根对象不含 nodes 数组（UI 格式特征）。</summary>
    public static bool IsApiFormat(string workflowJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(workflowJson);
            return doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 转换 UI 格式工作流为 API 格式。已是 API 格式时原样返回。
    /// objectInfo 为 null 或节点类型缺失时尽力转换（缺失类型节点仅填连线输入）。
    /// 失败返回 null（调用方提示手动粘贴 API 格式）。
    /// </summary>
    /// <summary>ComfyUI 连线类型集合（UI input.type 判定用；动态输入展开后的子槽同样属于这些类型）。</summary>
    private static readonly HashSet<string> ComfyLinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODEL", "CLIP", "VAE", "CONDITIONING", "IMAGE", "LATENT", "CONTROL_NET", "MASK"
    };

    private static bool IsComfyLinkType(string? type)
        => !string.IsNullOrEmpty(type) && ComfyLinkTypes.Contains(type);

    /// <summary>API widget 输入类型白名单：仅这些类型消费 widgets_values 槽位（其他如 RESOLUTION_PREVIEW 展示控件不占槽）。</summary>
    /// 除基础标量外，包含 COMFY_DYNAMICCOMBO_V3 等自定义节点动态下拉（如 SaveImageAdvanced.format），
    /// 否则会漏配字段导致执行时缺参（如 missing format）。
    private static bool IsApiWidgetType(string? type)
        => type != null
           && (type.Equals("STRING", StringComparison.OrdinalIgnoreCase)
               || type.Equals("INT", StringComparison.OrdinalIgnoreCase)
               || type.Equals("FLOAT", StringComparison.OrdinalIgnoreCase)
               || type.Equals("COMBO", StringComparison.OrdinalIgnoreCase)
               || type.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)
               || type.Equals("COMFY_DYNAMICCOMBO_V3", StringComparison.OrdinalIgnoreCase)
               || type.Equals("COMFY_DYNAMICCOMBO", StringComparison.OrdinalIgnoreCase)
               || type.Equals("COMFY_STRING", StringComparison.OrdinalIgnoreCase)
               || type.Equals("COMFY_INT", StringComparison.OrdinalIgnoreCase)
               || type.Equals("COMFY_FLOAT", StringComparison.OrdinalIgnoreCase));

    /// <summary>seed 复合控件的 control 状态字面量（前端 widget 槽位，API 无此字段）。</summary>
    private static bool IsControlAfterGenerateLiteral(string? value)
        => value != null
           && (value.Equals("randomize", StringComparison.OrdinalIgnoreCase)
               || value.Equals("fixed", StringComparison.OrdinalIgnoreCase)
               || value.Equals("increment", StringComparison.OrdinalIgnoreCase)
               || value.Equals("decrement", StringComparison.OrdinalIgnoreCase));
    public static string? ConvertToApi(string workflowJson, ObjectInfoResponse? objectInfo)
    {
        try
        {
            if (IsApiFormat(workflowJson)) return workflowJson;

            using var doc = JsonDocument.Parse(workflowJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;

            // links: [linkId, fromNode, fromSlot, toNode, toSlot, type]
            var links = new Dictionary<long, JsonElement>();
            if (root.TryGetProperty("links", out var linksEl) && linksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in linksEl.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Array || link.GetArrayLength() < 2) continue;
                    if (link[0].ValueKind == JsonValueKind.Number)
                        links[link[0].GetInt64()] = link;
                }
            }

            // 预扫描：过滤 object_info 中不存在的节点类型（如前端注释节点 MarkdownNote/Note），
            // 它们无需后端执行，提交会被 ComfyUI 以 missing_node_type 拒绝。objectInfo 为 null 时不过滤。
            var skippedIds = new HashSet<string>();
            if (objectInfo != null)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    if (node.ValueKind != JsonValueKind.Object) continue;
                    if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                    var classType = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : null;
                    if (string.IsNullOrEmpty(classType)) continue;
                    if (!objectInfo.Nodes.ContainsKey(classType))
                        skippedIds.Add(idEl.GetInt64().ToString());
                }
            }

            var api = new Dictionary<string, object>();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                var id = idEl.GetInt64().ToString();
                if (skippedIds.Contains(id)) continue;
                var classType = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
                if (string.IsNullOrEmpty(classType)) continue;

                var inputs = new Dictionary<string, object>();

                // widget 值：按 node.inputs 保存顺序与 widgets_values 一一对应（与 ComfyUI 前端转换逻辑一致）。
                // 注意：widgets_values 含 control_after_generate 等前端控件槽位（API 无此字段，消费时跳过不写），
                // 因此不能按 object_info 字段序映射，否则 seed 之后的字段整体错位（如 steps 收到 randomize、缺 denoise）。
                var widgetIdx = 0;
                var widgetValues = node.TryGetProperty("widgets_values", out var widgets) && widgets.ValueKind == JsonValueKind.Array
                    ? widgets.EnumerateArray().ToList()
                    : new List<JsonElement>();

                if (node.TryGetProperty("inputs", out var nodeInputs) && nodeInputs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var input in nodeInputs.EnumerateArray())
                    {
                        if (input.ValueKind != JsonValueKind.Object) continue;
                        if (!input.TryGetProperty("name", out var inName) || inName.ValueKind != JsonValueKind.String) continue;
                        var name = inName.GetString()!;

                        // 输入类型判定：API widget 类型（STRING/INT/FLOAT/COMBO/BOOLEAN）才消费 widgets_values；
                        // 连线类型（MODEL/IMAGE/...）与展示控件（RESOLUTION_PREVIEW 等）不占 widget 槽位。
                        var inTypeStr = input.TryGetProperty("type", out var inTypeEl) && inTypeEl.ValueKind == JsonValueKind.String
                            ? inTypeEl.GetString() : null;
                        var isApiWidget = IsApiWidgetType(inTypeStr);

                        // 连线输入：link 存在于 links 表 → [源节点id, 源输出槽]；否则该槽位是 widget 值
                        var isLink = input.TryGetProperty("link", out var linkId)
                            && linkId.ValueKind == JsonValueKind.Number
                            && links.ContainsKey(linkId.GetInt64());
                        if (isLink)
                        {
                            var link = links[linkId.GetInt64()];
                            if (link.GetArrayLength() < 4) continue;
                            if (link[1].ValueKind == JsonValueKind.Number && link[2].ValueKind == JsonValueKind.Number)
                            {
                                var fromNode = link[1].GetInt64().ToString();
                                // 源节点已被跳过（如注释节点），该连线输入不填，避免引用不存在的节点
                                if (skippedIds.Contains(fromNode)) continue;
                                var fromSlot = link[2].GetInt32();
                                inputs[name] = new object[] { fromNode, fromSlot };
                            }
                            // 被连线的 widget（如 EmptyLatentImage 的 width/height 被连线但值仍在 widgets_values 中）
                            // 仍占槽位：跳过其值，否则后续 widget 整体错位（如 batch_size 收到 1024 → 显存爆掉）
                            if (isApiWidget) widgetIdx++;
                            continue;
                        }

                        // 未连接的可选连线输入或展示控件（RESOLUTION_PREVIEW 等）：不占槽位、不写入 API
                        if (!isApiWidget) continue;

                        // 显式的 control_after_generate 输入项（少数工作流 JSON 存在）：占位但不写入 API
                        if (name == "control_after_generate")
                        {
                            widgetIdx++;
                            continue;
                        }

                        if (widgetIdx < widgetValues.Count)
                        {
                            var val = widgetValues[widgetIdx];
                            // seed 类 INT 字段可能收到 "randomize" 等前端控件字面值 → 生成随机整数兜底
                            if (val.ValueKind == JsonValueKind.String
                                && (name.Contains("seed", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(val.GetString(), "randomize", StringComparison.OrdinalIgnoreCase)))
                            {
                                inputs[name] = Random.Shared.NextInt64();
                            }
                            else
                            {
                                inputs[name] = CloneJsonValue(val);
                            }
                            widgetIdx++;
                        }
                        // seed 复合控件：widgets_values 中紧随的 control 状态字面量（randomize/fixed/increment/decrement）
                        // 是前端控件槽位，API 无此字段，消费后跳过
                        while (widgetIdx < widgetValues.Count
                            && widgetValues[widgetIdx].ValueKind == JsonValueKind.String
                            && IsControlAfterGenerateLiteral(widgetValues[widgetIdx].GetString()))
                        {
                            widgetIdx++;
                        }
                    }
                }

                api[id] = new Dictionary<string, object>
                {
                    ["class_type"] = classType,
                    ["inputs"] = inputs,
                };
            }

            return JsonSerializer.Serialize(api);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object CloneJsonValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null!,
        JsonValueKind.Object or JsonValueKind.Array => el.Clone(),
        _ => null!,
    };

    /// <summary>
    /// 应用参数配置到 UI 工作流 JSON（详情页"配置参数"弹窗保存的条目）。
    /// 槽位定位与 <see cref="ConvertToApi"/> 完全一致：按 node.inputs 顺序消费 widgets_values，
    /// 连线 widget 仍占槽、control_after_generate 槽位跳过——保证覆盖后转换不产生错位。
    /// 命中项写入固定值或随机种子（seed 复合控件紧随的 control 字面量槽位不动）。
    /// 返回修改后的 JSON；解析失败或无可应用项返回原 JSON。
    /// </summary>
    public static string ApplyParams(string workflowJson, List<WorkflowParamEntry> entries)
    {
        if (entries == null || entries.Count == 0 || string.IsNullOrEmpty(workflowJson)) return workflowJson;
        try
        {
            if (IsApiFormat(workflowJson)) return workflowJson; // 参数覆盖只针对 UI 格式

            using var doc = JsonDocument.Parse(workflowJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return workflowJson;

            // 按节点 id 聚合配置项
            var byNode = new Dictionary<string, List<WorkflowParamEntry>>();
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.NodeId) || string.IsNullOrEmpty(e.FieldName)) continue;
                if (!byNode.TryGetValue(e.NodeId, out var list))
                    byNode[e.NodeId] = list = new List<WorkflowParamEntry>();
                list.Add(e);
            }
            if (byNode.Count == 0) return workflowJson;

            var mutable = JsonNode.Parse(workflowJson);
            if (mutable is not JsonObject mutableRoot) return workflowJson;
            if (mutableRoot["nodes"] is not JsonArray mutableNodes) return workflowJson;

            foreach (var node in mutableNodes.OfType<JsonObject>())
            {
                if (node["id"] is not JsonValue idv || !idv.TryGetValue<int>(out var idInt)) continue;
                var id = idInt.ToString();
                if (!byNode.TryGetValue(id, out var nodeEntries)) continue;

                // 新版 ComfyUI 格式：widgets_values_named（{输入名: 值}）——直接按名字覆盖，无需槽位换算；
                // named 为权威值来源，覆盖后整体跳过旧兼容数组槽位（避免错位覆盖）
                if (node["widgets_values_named"] is JsonObject named)
                {
                    foreach (var e in nodeEntries)
                    {
                        if (string.IsNullOrEmpty(e.FieldName)) continue;
                        if (named[e.FieldName] is JsonNode target)
                            named[e.FieldName] = BuildParamValue(target, e);
                    }
                    continue;
                }

                if (node["widgets_values"] is not JsonArray wv) continue;
                if (node["inputs"] is not JsonArray inputs) continue;

                var widgetIdx = 0;
                var appliedAny = false;
                foreach (var input in inputs.OfType<JsonObject>())
                {
                    if (input["name"] is not JsonValue nv || !nv.TryGetValue<string>(out var name)) continue;
                    var type = input["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
                    var isApiWidget = IsApiWidgetType(type);
                    var isLink = input["link"] is JsonValue lv && lv.TryGetValue<int>(out _);

                    if (isLink) // 连线输入：被连线的 widget 仍占槽位（与转换器一致）
                    {
                        if (isApiWidget) widgetIdx++;
                        continue;
                    }
                    if (!isApiWidget) continue; // 展示控件/未连接的连线输入不占槽
                    if (name == "control_after_generate") { widgetIdx++; continue; }

                    var entry = nodeEntries.FirstOrDefault(x => x.FieldName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (entry != null && widgetIdx < wv.Count)
                    {
                        wv[widgetIdx] = BuildParamValue(wv[widgetIdx], entry);
                        appliedAny = true;
                    }
                    widgetIdx++;

                    // 跳过 seed 复合控件紧随的 control 状态字面量槽位（randomize/fixed/...）
                    while (widgetIdx < wv.Count
                        && wv[widgetIdx] is JsonValue cv
                        && cv.TryGetValue<string>(out var s)
                        && IsControlAfterGenerateLiteral(s))
                    {
                        widgetIdx++;
                    }
                }

                if (appliedAny)
                    _ = id; // 占位：保持可读性（byNode 命中即说明有配置）
            }

            return mutableRoot.ToJsonString();
        }
        catch (Exception)
        {
            return workflowJson; // 解析失败保持原样，不阻塞执行
        }
    }

    /// <summary>把配置项换算成要写入 widgets_values 槽位的 JSON 值（按原值类型转换，避免类型漂移）。</summary>
    private static JsonNode? BuildParamValue(JsonNode? original, WorkflowParamEntry entry)
    {
        if (entry.UseRandom)
        {
            // ComfyUI Seed 为 64 位无符号范围（0 ~ 2^64-1），用 64 位随机数在 [min, max] 内取值（含边界）
            var rand = NextRandomInRange(entry.RandomMin, entry.RandomMax);
            return JsonValue.Create(rand);
        }
        if (entry.Value == null) return original?.DeepClone();

        if (original is JsonValue ov)
        {
            if (ov.TryGetValue<bool>(out _))
                return JsonValue.Create(entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (ov.TryGetValue<int>(out _) && long.TryParse(entry.Value, out var li))
                return JsonValue.Create((int)li);
            if (ov.TryGetValue<long>(out _) && long.TryParse(entry.Value, out var ll))
                return JsonValue.Create(ll);
            if (ov.TryGetValue<double>(out _) && double.TryParse(entry.Value, out var d))
                return JsonValue.Create(d);
        }
        return JsonValue.Create(entry.Value);
    }

    /// <summary>在 [min, max]（闭区间，ulong 全范围）内生成均匀随机值：8 字节随机 + 取模；全范围时直接返回随机 64 位。</summary>
    private static ulong NextRandomInRange(ulong min, ulong max)
    {
        Span<byte> buf = stackalloc byte[8];
        Random.Shared.NextBytes(buf);
        var rand = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf);
        var span = max - min; // max >= min，不溢出
        if (span == ulong.MaxValue) return rand; // 覆盖全范围，任何值都合法
        return min + (rand % (span + 1));
    }
}

/// <summary>
/// ComfyUI WebSocket 客户端：连接 ws://{host}/ws?clientId=xxx，接收执行进度消息。
/// 只负责接收与按 prompt_id 过滤；进度展示由上层订阅 <see cref="MessageReceived"/> 处理。
/// 断线/异常触发 <see cref="Disconnected"/>，调用方应切换轮询兜底。
/// </summary>
public class ComfyUIWebSocketClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly IBaseLogService _log;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly object _sync = new();

    /// <summary>是否为主动断开（Disconnect / 重连前清理）：主动断开不上报 <see cref="Disconnected"/>，避免把停止/重连误报成"连接丢失"。</summary>
    private volatile bool _intentionalClose;

    public ComfyUIWebSocketClient(ComfySettings settings, IBaseLogService? log = null)
    {
        _baseUrl = settings.ComfyApiUrl.TrimEnd('/');
        _log = log ?? PromptCraft.Service.LogService.Instance;
    }

    /// <summary>收到执行消息（后台线程触发，UI 需自行 Dispatcher 封送）。</summary>
    public event Action<WsExecutionMessage>? MessageReceived;

    /// <summary>连接断开（参数为错误消息，正常关闭时为 null）。</summary>
    public event Action<string?>? Disconnected;

    public bool IsConnected
    {
        get { lock (_sync) return _ws?.State == WebSocketState.Open; }
    }

    /// <summary>建立连接并启动接收循环。重复调用会先断开旧连接。</summary>
    public async Task<bool> ConnectAsync(string clientId, CancellationToken ct = default)
    {
        Disconnect();

        // 自动区分 ws/wss：按配置地址 scheme（http→ws / https→wss），Authority 含端口（默认端口省略）
        var baseUri = new Uri(_baseUrl);
        var scheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var wsUrl = $"{scheme}://{baseUri.Authority}/ws?clientId={Uri.EscapeDataString(clientId)}";
        try
        {
            var ws = new ClientWebSocket();
            // 连接阶段限时 8 秒（失败返回 false，调用方退化为轮询）；接收循环不受此超时影响
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);
            lock (_sync) _ws = ws;

            _intentionalClose = false; // 新会话已建立：此后意外关闭（如 ComfyUI 退出）才上报 Disconnected
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(ws, _cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            // 单次连接失败不写 Warn：重试策略与日志由 Hub 统一管理，仅在最终连接成功后记一条（含尝试次数），
            // 避免 ComfyUI 未运行时日志被逐次重试刷屏；这里保留 Debug 供调低日志级别时排查失败原因
            _log.Debug(string.Format(Localizer.Instance?["WsConnectFailed"] ?? "", wsUrl), "ComfyUI", ex);
            return false;
        }
    }

    public void Disconnect()
    {
        _intentionalClose = true; // 主动断开：接收循环不再上报 Disconnected（避免应用退出 / 重连前清理时误报"连接丢失"）
        CancellationTokenSource? cts;
        ClientWebSocket? ws;
        lock (_sync)
        {
            cts = _cts;
            ws = _ws;
            _cts = null;
            _ws = null;
        }

        try { cts?.Cancel(); } catch { }
        try { ws?.Dispose(); } catch { }
        try { _receiveTask?.Wait(TimeSpan.FromMilliseconds(300)); } catch { }
        _receiveTask = null;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // 二进制帧是预览图，跳过
                        if (result.EndOfMessage) break;
                        continue;
                    }
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // 远端关闭（如 ComfyUI 退出）：非主动断开才上报，供上层弹"连接已断开"提示
                        if (!_intentionalClose) Disconnected?.Invoke(null);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (ms.Length == 0) continue;
                ParseMessage(ms.ToArray());
            }
            if (!_intentionalClose) Disconnected?.Invoke(null);
        }
        catch (OperationCanceledException)
        {
            // 主动断开
        }
        catch (Exception ex)
        {
            if (_intentionalClose) return; // 主动断开导致的异常（如 Dispose 中的 ObjectDisposedException），不上报
            _log.Debug(Localizer.Instance?["WsReceiveException"] ?? "", "ComfyUI", ex);
            Disconnected?.Invoke(ex.Message);
        }
    }

    private void ParseMessage(byte[] payload)
    {
        try
        {
            var text = Encoding.UTF8.GetString(payload);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return;
            var type = typeEl.GetString() ?? "";
            var data = root.TryGetProperty("data", out var d) ? d : default;

            // status 消息没有 prompt_id，走系统状态（这里忽略）
            var promptId = "";
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("prompt_id", out var pid)
                && pid.ValueKind == JsonValueKind.String)
                promptId = pid.GetString() ?? "";

            if (MessageReceived != null)
                MessageReceived(new WsExecutionMessage(type, promptId, data.Clone()));
        }
        catch (Exception ex)
        {
            _log.Debug(Localizer.Instance?["WsParseFailed"] ?? "", "ComfyUI", ex);
        }
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}