using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace PromptCraft.Service;

/// <summary>
/// ComfyUI 业务服务类：处理 API 调用、工作流同步/执行/删除与数据持久化。
/// 所有关键操作均有日志埋点（category 统一用 "Workflow" / "ComfyUI"）。
/// </summary>
public class ComfyUIService : IComfyUIService
{
    private readonly IComfyUIClient _client;
    private readonly ComfySettings _settings;
    private readonly IComfyUIWebSocketHub _hub;
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IImageMetadataRepository _imageRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IJobRepository _jobRepo;
    private readonly IJobOutputRepository _jobOutputRepo;
    private readonly IImageSyncService _imageSync; // 复用其缩略图生成（自动入库时生成缩略图文件）
    private readonly IBaseLogService _log;

    public ComfyUIService(
        IComfyUIClient client,
        ComfySettings settings,
        IComfyUIWebSocketHub hub,
        IDbContextFactory<ComfyDbContext> dbFactory,
        IImageMetadataRepository imageRepo,
        IWorkflowRepository workflowRepo,
        IJobRepository jobRepo,
        IJobOutputRepository jobOutputRepo,
        IImageSyncService imageSync,
        IBaseLogService log)
    {
        _client = client;
        _settings = settings;
        _hub = hub;
        _dbFactory = dbFactory;
        _imageRepo = imageRepo;
        _workflowRepo = workflowRepo;
        _jobRepo = jobRepo;
        _imageSync = imageSync;
        _jobOutputRepo = jobOutputRepo;
        _log = log;
    }

    /// <summary>执行进度变化事件（后台线程触发，UI 需 Dispatcher 封送）。按 JobId 区分任务。</summary>
    public event Action<WorkflowJobProgress>? JobProgressChanged;

    private void NotifyProgress(int jobId, string status, int value = 0, int max = 0, string? logLine = null, string? error = null)
    {
        try
        {
            JobProgressChanged?.Invoke(new WorkflowJobProgress
            {
                JobId = jobId,
                Status = status,
                ProgressValue = value,
                ProgressMax = max,
                NodeLogLine = logLine,
                ErrorMessage = error,
            });
        }
        catch (Exception ex)
        {
            _log.Debug(Localizer.Instance?["NotifyProgressException"] ?? "", "Workflow", ex);
        }
    }

    #region 队列状态 / 节点信息

    /// <summary>获取当前队列状态</summary>
    public async Task<QueueStatusResponse?> GetQueueStatusAsync()
    {
        return await _client.GetQueueStatusAsync();
    }

    /// <summary>获取所有节点定义（供 UI→API 转换）</summary>
    public async Task<ObjectInfoResponse?> GetObjectInfoAsync()
    {
        return await _client.GetObjectInfoAsync();
    }

    #endregion

    #region Workflow 同步（从 ComfyUI 拉取）

    /// <summary>
    /// 从配置的 ComfyUI 拉取全部工作流文件并保存到本地数据库。
    /// 按 SourcePath（ComfyUI 文件相对路径）upsert：已存在更新、不存在新增。
    /// </summary>
    public async Task<WorkflowSyncResult> SyncWorkflowsFromComfyAsync()
    {
        var result = new WorkflowSyncResult();
        _log.Info(Localizer.Instance?["SyncWorkflowsStart"] ?? "", "Workflow");
        try
        {
            var files = await _client.ListUserDataWorkflowsAsync();
            _log.Debug(string.Format(Localizer.Instance?["WorkflowFilesCount"] ?? "", files.Count), "Workflow");

            foreach (var file in files)
            {
                try
                {
                    if (!file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Debug(string.Format(Localizer.Instance?["SkipNonJsonFile"] ?? "", file.Name), "Workflow");
                        continue;
                    }

                    var content = await _client.GetUserDataFileAsync(file.Name);
                    if (string.IsNullOrEmpty(content))
                    {
                        result.Errors++;
                        result.ErrorMessages.Add(string.Format(Localizer.Instance?["ReadFileFailed"] ?? "", file.Name));
                        _log.Warn(string.Format(Localizer.Instance?["SyncReadFailed"] ?? "", file.Name), "Workflow");
                        continue;
                    }

                    // path 可能含子目录（recurse=true），取末段作为工作流名；时间戳为 Unix 毫秒
                    var name = Path.GetFileNameWithoutExtension(file.Name.Split('/').Last());
                    var updatedAt = file.MTime > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(file.MTime).UtcDateTime
                        : DateTime.UtcNow;

                    var (added, workflow) = await _workflowRepo.UpsertFromComfyAsync(file.Name, name, content, updatedAt);
                    if (added)
                    {
                        result.New++;
                        _log.Info(string.Format(Localizer.Instance?["WorkflowAdded"] ?? "", name, file.Name), "Workflow");
                    }
                    else
                    {
                        result.Updated++;
                        _log.Info(string.Format(Localizer.Instance?["WorkflowUpdated"] ?? "", name, file.Name), "Workflow");
                    }
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    result.ErrorMessages.Add(string.Format(Localizer.Instance?["FileErrorEntry"] ?? "", file.Name, ex.Message));
                    _log.Error(string.Format(Localizer.Instance?["SyncWorkflowException"] ?? "", file.Name), "Workflow", ex);
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors++;
            result.ErrorMessages.Add(string.Format(Localizer.Instance?["SyncProcessException"] ?? "", ex.Message));
            _log.Error(Localizer.Instance?["SyncWorkflowsFailed"] ?? "", "Workflow", ex);
        }

        _log.Info(string.Format(Localizer.Instance?["WorkflowSyncDone"] ?? "", result.New, result.Updated, result.Errors), "Workflow");
        return result;
    }

    #endregion

    #region Workflow 执行（提交 + 进度跟踪）

    /// <summary>
    /// 提交并跟踪工作流执行：
    /// 1) UI 格式自动转 API 格式（/prompt 只接受 API 格式）；
    /// 2) POST /prompt（携带自建 client_id / prompt_id）；
    /// 3) 写入 WorkflowJob 执行记录；
    /// 4) 后台 WebSocket 订阅进度（断线退化为轮询 /history），更新 Job 状态与节点日志；
    /// 5) 完成后从 /history 取输出图片写入 JobOutput。
    /// </summary>
    public async Task<SubmitWorkflowResponse> SubmitAndTrackWorkflowAsync(int workflowId, string? overrideUiJson = null)
    {
        _log.Info(string.Format(Localizer.Instance?["SubmitStart"] ?? "", workflowId), "Workflow");
        var workflow = await _workflowRepo.GetByIdAsync(workflowId);
        if (workflow == null)
        {
            _log.Warn(string.Format(Localizer.Instance?["SubmitWorkflowMissing"] ?? "", workflowId), "Workflow");
            return new SubmitWorkflowResponse { Success = false, ErrorMessage = Localizer.Instance?["WorkflowNotFound"] ?? "" };
        }

        // 参数配置覆盖：overrideUiJson 非空（详情页"配置参数"应用后的 JSON）时优先使用；默认读压缩 Blob 解压
        var uiJson = string.IsNullOrEmpty(overrideUiJson) ? (workflow.GetWorkflowJson() ?? "") : overrideUiJson;

        // 1) 格式转换
        var apiJson = await ConvertToApiJsonAsync(workflow.Name, uiJson);
        if (apiJson == null)
        {
            return new SubmitWorkflowResponse { Success = false, ErrorMessage = Localizer.Instance?["ConvertFailedHint"] ?? "" };
        }

        // 2)+3)+4) 提交 + 执行记录 + 后台跟踪（带上 UI 格式 JSON，写入 extra_pnginfo 使 PNG 携带工作流元数据）
        return await SubmitCoreAsync(workflow.Name, workflowId, apiJson, uiJson);
    }

    /// <summary>
    /// 提交并跟踪"图片提取"工作流（未物化为 Workflows 记录）：
    /// 先在 Workflows 表物化一条记录（执行历史需要归属），再走同一套提交/跟踪流程。
    /// 返回提交结果与物化后的 WorkflowId（未执行成功时 WorkflowId 也可能已创建）。
    /// </summary>
    public async Task<(SubmitWorkflowResponse Response, int WorkflowId)> SubmitAndTrackImageWorkflowAsync(string workflowJson, string title)
    {
        _log.Info(string.Format(Localizer.Instance?["SubmitImageWorkflowStart"] ?? "", title), "Workflow");

        var apiJson = await ConvertToApiJsonAsync(title, workflowJson);
        if (apiJson == null)
        {
            return (new SubmitWorkflowResponse { Success = false, ErrorMessage = Localizer.Instance?["ConvertFailedHint"] ?? "" }, 0);
        }

        var workflowId = await MaterializeWorkflowAsync(title, workflowJson);
        var submit = await SubmitCoreAsync(title, workflowId, apiJson, workflowJson);
        return (submit, workflowId);
    }

    /// <summary>UI 格式 → API 格式（/prompt 只接受 API 格式）；已是 API 格式则原样返回。</summary>
    private async Task<string?> ConvertToApiJsonAsync(string name, string workflowJson)
    {
        if (WorkflowJsonConverter.IsApiFormat(workflowJson))
        {
            _log.Debug(string.Format(Localizer.Instance?["AlreadyApiFormat"] ?? "", name), "Workflow");
            return workflowJson;
        }

        var objectInfo = await _client.GetObjectInfoAsync();
        var converted = WorkflowJsonConverter.ConvertToApi(workflowJson, objectInfo);
        if (converted == null)
        {
            _log.Warn(string.Format(Localizer.Instance?["ConvertFailedManual"] ?? "", name), "Workflow");
            return null;
        }
        _log.Debug(string.Format(Localizer.Instance?["ConvertDone"] ?? "", name), "Workflow");
        return converted;
    }

    /// <summary>
    /// 物化 Workflows 记录（图片提取工作流执行历史归属）：按顶级 WorkflowGuid 查重复用；
    /// 已存在（未删除）则复用。工作流 JSON GZip 压缩存 <see cref="Workflow.WorkflowJsonBlob"/>。
    /// </summary>
    private async Task<int> MaterializeWorkflowAsync(string name, string workflowJson)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        // 图片提取工作流必有顶级 UUID（AttachMetadataAsync 入库前提）；按 WorkflowGuid 唯一索引查重，
        // 语义比原"明文 JSON 内容相等"更准（内容相同但 UUID 不同的工作流不再误复用），且无需解压比对。
        var wfGuid = ExtractWorkflowGuid(workflowJson);
        if (wfGuid != null)
        {
            var existing = await db.Workflows
                .FirstOrDefaultAsync(x => x.WorkflowGuid == wfGuid && (x.Source == "image" || x.Source == "video") && !x.IsDeleted);
            if (existing != null)
            {
                _log.Debug(string.Format(Localizer.Instance?["MaterializedReuse"] ?? "", existing.Id, name), "Workflow");
                return existing.Id;
            }
        }

        var workflow = new Workflow
        {
            Name = name,
            WorkflowJsonBlob = ComfyMetadataCodec.Compress(workflowJson),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        _log.Info(string.Format(Localizer.Instance?["Materialized"] ?? "", workflow.Id, name), "Workflow");
        return workflow.Id;
    }

    /// <summary>从工作流 JSON 提取顶级 "id"（ComfyUI 工作流 UUID）；无则返回 null</summary>
    private static string? ExtractWorkflowGuid(string workflowJson)
    {
        if (string.IsNullOrWhiteSpace(workflowJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(workflowJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var s = id.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>提交 + 写执行记录 + 后台跟踪（工作流同步/图片提取共用）。</summary>
    private async Task<SubmitWorkflowResponse> SubmitCoreAsync(string name, int workflowId, string apiJson, string uiJson)
    {
        // 复用应用级长连接会话的固定 client_id（启动时已尝试连接；未连上时跟踪退化为轮询）
        var clientId = _hub.ClientId;

        // prompt_id 由 ComfyUI 服务端生成（响应中返回）
        var submit = await _client.SubmitWorkflowAsync(apiJson, clientId, uiJson);
        if (!submit.Success)
        {
            _log.Error(string.Format(Localizer.Instance?["SubmitFailedLog"] ?? "", name, submit.ErrorMessage), "Workflow");
            return submit;
        }

        var job = new WorkflowJob
        {
            WorkflowId = workflowId,
            InputJson = apiJson,
            PromptId = submit.PromptId,
            Status = "queued",
            CreatedAt = DateTime.UtcNow,
        };
        await _jobRepo.AddAsync(job);
        _log.Info(string.Format(Localizer.Instance?["SubmitSuccessLog"] ?? "", name, job.Id, submit.PromptId), "Workflow");

        _ = Task.Run(() => TrackJobAsync(job.Id, submit.PromptId!, _hub));
        return submit;
    }

    /// <summary>后台跟踪一次执行：订阅应用级长连接会话（WS 实时进度）+ 轮询兜底 + 结果落库。</summary>
    private async Task TrackJobAsync(int jobId, string promptId, IComfyUIWebSocketHub hub)
    {
        _log.Info(string.Format(Localizer.Instance?["TrackStart"] ?? "", jobId, promptId), "Workflow");
        var nodeLog = new List<object>();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMsg(WsExecutionMessage msg) => OnWsMessage(jobId, promptId, msg, nodeLog, finished);
        hub.MessageReceived += OnMsg;
        if (!hub.IsConnected)
        {
            // WS 未连接：不立即收尾——下方轮询直到 /history 终态（轮询不依赖 WS）。
            // 否则执行中途就被标记完成，DB/UI 状态停留在排队中、也无终态提示。
            _log.Warn(string.Format(Localizer.Instance?["WsNotConnected"] ?? "", jobId), "Workflow");
        }

        try
        {

        // 轮询兜底：WS 正常时等待终态事件（每 2s 查一次 /history 双保险）；WS 断线时轮询直至终态
        while (!finished.Task.IsCompleted)
        {
            var done = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            if (done == finished.Task) break;

            try
            {
                var history = await _client.GetHistoryAsync(promptId);
                // 记录存在即执行已结束（成功或失败），立即收尾：
                // 若只认 Completed=true，失败执行（completed=false）会导致轮询永不结束、状态一直停在排队中
                if (history != null)
                {
                    finished.TrySetResult(true);
                    break;
                }
            }
            catch (Exception ex)
            {
                _log.Debug(string.Format(Localizer.Instance?["PollHistoryException"] ?? "", jobId, promptId), "Workflow", ex);
            }
        }

            await FinalizeJobAsync(jobId, promptId, nodeLog);
        }
        finally
        {
            hub.MessageReceived -= OnMsg;
        }
    }

    /// <summary>处理一条 WS 消息：更新 Job 状态、进度与节点日志。</summary>
    private void OnWsMessage(int jobId, string promptId, WsExecutionMessage msg, List<object> nodeLog, TaskCompletionSource<bool> finished)
    {
        if (!string.IsNullOrEmpty(msg.PromptId) && msg.PromptId != promptId)
            return; // 过滤其他任务的消息

        try
        {
            switch (msg.Type)
            {
                case "execution_start":
                    _log.Info(string.Format(Localizer.Instance?["ExecutionStart"] ?? "", jobId, promptId), "Workflow");
                    UpdateJobAsync(jobId, j => { j.Status = "running"; j.StartedAt = DateTime.UtcNow; });
                    nodeLog.Add(new { type = "execution_start", time = DateTime.UtcNow });
                    NotifyProgress(jobId, "running", 0, 0, Localizer.Instance?["ExecutionStartShort"] ?? "");
                    break;

                case "executing":
                    if (msg.Data.ValueKind == JsonValueKind.Object && msg.Data.TryGetProperty("node", out var node)
                        && node.ValueKind == JsonValueKind.Number)
                    {
                        var nodeId = node.GetInt64();
                        _log.Debug(string.Format(Localizer.Instance?["ExecutingNodeLog"] ?? "", jobId, nodeId), "Workflow");
                        nodeLog.Add(new { type = "executing", node = nodeId, time = DateTime.UtcNow });
                        NotifyProgress(jobId, "running", 0, 0, string.Format(Localizer.Instance?["ExecutingNodeNotify"] ?? "", nodeId));
                    }
                    break;

                case "progress":
                    if (msg.Data.ValueKind == JsonValueKind.Object
                        && msg.Data.TryGetProperty("value", out var v) && msg.Data.TryGetProperty("max", out var m)
                        && v.ValueKind == JsonValueKind.Number && m.ValueKind == JsonValueKind.Number)
                    {
                        var value = v.GetInt64();
                        var max = m.GetInt64();
                        _log.Debug(string.Format(Localizer.Instance?["ProgressLog"] ?? "", jobId, value, max), "Workflow");
                        nodeLog.Add(new { type = "progress", value, max, time = DateTime.UtcNow });
                        NotifyProgress(jobId, "running", (int)value, (int)max, string.Format(Localizer.Instance?["ProgressNotify"] ?? "", value, max));
                    }
                    break;

                case "executed":
                    if (msg.Data.ValueKind == JsonValueKind.Object && msg.Data.TryGetProperty("node", out var doneNode)
                        && doneNode.ValueKind == JsonValueKind.Number)
                    {
                        _log.Debug(string.Format(Localizer.Instance?["NodeDoneLog"] ?? "", jobId, doneNode.GetInt64()), "Workflow");
                        nodeLog.Add(new { type = "executed", node = doneNode.GetInt64(), time = DateTime.UtcNow });
                        NotifyProgress(jobId, "running", 0, 0, string.Format(Localizer.Instance?["NodeDoneNotify"] ?? "", doneNode.GetInt64()));
                    }
                    break;

                case "execution_success":
                    _log.Info(string.Format(Localizer.Instance?["ExecutionSuccessLog"] ?? "", jobId, promptId), "Workflow");
                    nodeLog.Add(new { type = "execution_success", time = DateTime.UtcNow });
                    // 先快速落库终态（纯 DB 写，毫秒级），再通知 UI：
                    // 消除"UI 已显示成功但 DB 还是 queued"的空窗（原空窗要等 FinalizeJobAsync 查 /history 后才保存，约 0.4s），
                    // 保证任何时刻读 DB（重开详情/刷新/双击）看到的都是最新状态，UI 与数据库一致。
                    UpdateJobAsync(jobId, j => { j.Status = "completed"; j.CompletedAt = DateTime.UtcNow; });
                    NotifyProgress(jobId, "completed", 0, 0, Localizer.Instance?["ExecutionSuccessShort"] ?? "");
                    finished.TrySetResult(true);
                    break;

                case "execution_error":
                case "execution_interrupted":
                    var err = msg.Data.ValueKind == JsonValueKind.Object && msg.Data.TryGetProperty("exception_message", out var em)
                        && em.ValueKind == JsonValueKind.String ? em.GetString() : Localizer.Instance?["ExecutionInterrupted"] ?? "";
                    _log.Warn(string.Format(Localizer.Instance?["ExecutionFailedLog"] ?? "", jobId, err), "Workflow");
                    nodeLog.Add(new { type = msg.Type, message = err, time = DateTime.UtcNow });
                    UpdateJobAsync(jobId, j => { j.Status = "failed"; j.ErrorMessage = err; j.CompletedAt = DateTime.UtcNow; });
                    NotifyProgress(jobId, "failed", 0, 0, Localizer.Instance?["ExecutionFailed"] ?? "", err);
                    finished.TrySetResult(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(Localizer.Instance?["WsMessageException"] ?? "", jobId), "Workflow", ex);
        }
    }

    /// <summary>
    /// 执行结束收尾：持久化节点日志 + job 终态（completed/failed）+ 输出记录 JobOutput。
    /// <b>job 状态保存与自动入库完全解耦</b>：先独立保存 job/outputs（轻量、无外键风险），
    /// 再单独跑入库（独立 DbContext）——入库无论成败都不影响 job 终态落库，
    /// 避免过去"入库失败拖垮整批 SaveChanges → 状态永远停在排队中"的问题。
    /// </summary>
    private async Task FinalizeJobAsync(int jobId, string promptId, List<object> nodeLog)
    {
        string? finalStatus = "completed";
        string? finalError = null;
        try
        {
            var history = await _client.GetHistoryAsync(promptId);
            var outputs = new List<JobOutput>();
            if (history != null)
            {
                foreach (var img in history.OutputImages)
                {
                    outputs.Add(new JobOutput
                    {
                        JobId = jobId,
                        FileName = img.FileName,
                        SubFolder = img.SubFolder,
                        PromptId = promptId,
                    });
                }
            }

            // ---- 第一步：保存 job 终态 + 输出记录（不涉及图库，必然可保存）----
            string? inputJson = null;
            using (var db = await _dbFactory.CreateDbContextAsync())
            {
                var job = await db.WorkflowJobs.FirstOrDefaultAsync(x => x.Id == jobId && !x.IsDeleted);
                if (job != null)
                {
                    inputJson = job.InputJson;
                    if (job.Status != "failed")
                    {
                        // 轮询兜底路径：history 记录存在即执行已结束；成功 completed，否则 failed（附错误信息）
                        job.Status = history is { Completed: true } ? "completed" : "failed";
                        job.CompletedAt = DateTime.UtcNow;
                        if (history is { Completed: false })
                            job.ErrorMessage = history.StatusMessage ?? Localizer.Instance?["ExecutionFailedNoDetail"] ?? "";
                    }
                    if (nodeLog.Count > 0)
                        job.NodeLogBlob = ComfyMetadataCodec.Compress(JsonSerializer.Serialize(nodeLog));
                    if (outputs.Count > 0)
                        job.Outputs = outputs;
                    await db.SaveChangesAsync();
                    finalStatus = job.Status;
                    finalError = job.ErrorMessage;
                }
                else
                {
                    finalStatus = history is { Completed: true } ? "completed" : "failed";
                    finalError = history is { Completed: false } ? (history.StatusMessage ?? Localizer.Instance?["ExecutionFailed"] ?? "") : null;
                }
            }

            if (outputs.Count > 0)
                _log.Info(string.Format(Localizer.Instance?["ExecutionDoneWithOutputs"] ?? "", outputs.Count, jobId, promptId), "Workflow");
            else
                _log.Info(string.Format(Localizer.Instance?["ExecutionDoneNoOutputs"] ?? "", jobId, promptId), "Workflow");

            // ---- 终态通知（DB 已落库）立即发出：不等待自动入库，UI 立刻刷新出 completed + 输出图 ----
            // 顺序原则：先更新数据库（job 终态 + 输出记录），再通知 UI；入库随后在后台完成，绝不阻塞状态显示。
            NotifyProgress(jobId, finalStatus, 0, 0, Localizer.Instance?["ResultPersisted"] ?? "", finalError);

            // ---- 第二步：输出图片自动加入图库（后台异步；独立 DbContext；失败只记日志，绝不影响 job 终态）----
            if (outputs.Count > 0 && jobId > 0)
            {
                var apiJsonForImport = inputJson;
                var outputsForImport = outputs;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await AddOutputImagesToGalleryAsync(jobId, outputsForImport, apiJsonForImport);
                    }
                    catch (Exception ex)
                    {
                        _log.Error(string.Format(Localizer.Instance?["AutoImportException"] ?? "", jobId), "Workflow", ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(Localizer.Instance?["FinalizeFailed"] ?? "", jobId, promptId), "Workflow", ex);
            // 收尾异常时仍通知终态，避免 UI 悬挂（状态以就地更新为准；DB 异常已记 Error 供排查）
            NotifyProgress(jobId, finalStatus, 0, 0, Localizer.Instance?["ResultPersisted"] ?? "", finalError);
        }
    }

    /// <summary>
    /// 执行成功后把输出图片加入图库（独立 DbContext，与 job 终态保存解耦）：
    /// 已入库（同 RelativePath）则回填 JobOutput.ImageInfoId；不存在则创建 ImageInfo 记录
    /// （含尺寸/哈希/关联工作流 + 提示词/参数，图片详情可直接查看）。
    /// 任何失败只记日志，绝不影响 job 状态。
    /// </summary>
    private async Task AddOutputImagesToGalleryAsync(int jobId, List<JobOutput> outputs, string? apiJson)
    {
        if (string.IsNullOrEmpty(_settings.ComfyOutputDir) || outputs.Count == 0) return;
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            // 提示词/参数：复用图片元数据提取器（API 格式 prompt 提取 KSampler/CLIPTextEncode 等），
            // 同一工作流内按 PromptHash 去重复用
            ImagePrompt? prompt = null;
            var workflowId = await db.WorkflowJobs.Where(x => x.Id == jobId).Select(x => x.WorkflowId).FirstOrDefaultAsync();
            if (workflowId == 0)
            {
                _log.Warn(string.Format(Localizer.Instance?["AutoImportSkipped"] ?? "", jobId), "Workflow");
                return;
            }
            if (!string.IsNullOrEmpty(apiJson))
            {
                try
                {
                    var doc = new ImageMetadataDocument
                    {
                        Prompt = ParseJsonElement(apiJson),
                        Workflow = null,
                        Extracted = new ExtractedMetadata(),
                    };
                    ComfyMetadataExtractor.Extract(doc);
                    if (doc.Prompt is JsonElement p)
                    {
                        var promptHash = ComfyMetadataCodec.ComputeJsonHash(p.GetRawText());
                        prompt = await db.ImagePrompts
                            .FirstOrDefaultAsync(x => x.WorkflowId == workflowId && x.PromptHash == promptHash && !x.IsDeleted);
                        if (prompt == null)
                        {
                            var ex = doc.Extracted;
                            prompt = new ImagePrompt
                            {
                                WorkflowId = workflowId,
                                PromptHash = promptHash,
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
                            db.ImagePrompts.Add(prompt);
                        }
                        else
                        {
                            // 复用已有提示词：老数据可能因提取规则升级而缺提示词/参数，用本次提取结果补全缺失字段
                            var ex = doc.Extracted;
                            if (ex != null)
                            {
                                prompt.PositivePrompt ??= ex.PositivePrompt;
                                prompt.NegativePrompt ??= ex.NegativePrompt;
                                prompt.Model ??= ex.Checkpoint;
                                prompt.Seed ??= ex.Seed;
                                prompt.Steps ??= ex.Steps;
                                prompt.Cfg ??= ex.Cfg;
                                prompt.Sampler ??= ex.Sampler;
                                prompt.Scheduler ??= ex.Scheduler;
                                prompt.Width ??= ex.Width;
                                prompt.Height ??= ex.Height;
                                if (ex is { LoraNames.Count: > 0 } && string.IsNullOrEmpty(prompt.LoraNames))
                                    prompt.LoraNames = string.Join(", ", ex.LoraNames);
                                if (prompt.NodesJsonBlob == null && ex is { Nodes.Count: > 0 })
                                    prompt.NodesJsonBlob = ComfyMetadataCodec.Compress(JsonSerializer.Serialize(ex.Nodes));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(string.Format(Localizer.Instance?["ExtractPromptFailed"] ?? "", jobId), "Workflow", ex);
                }
            }

            foreach (var output in outputs)
            {
                var sub = string.IsNullOrEmpty(output.SubFolder) ? "" : output.SubFolder.Replace('\\', '/') + "/";
                var rel = sub + output.FileName;
                var full = string.IsNullOrEmpty(output.SubFolder)
                    ? Path.Combine(_settings.ComfyOutputDir, output.FileName)
                    : Path.Combine(_settings.ComfyOutputDir, output.SubFolder, output.FileName);

                // 已入库（图库同步过/上一次执行已加）→ 只回填关联
                var img = await db.ImageMetadata.FirstOrDefaultAsync(x => x.RelativePath == rel);
                if (img == null)
                {
                    if (!File.Exists(full))
                    {
                        _log.Debug(string.Format(Localizer.Instance?["OutputFileMissing"] ?? "", full), "Workflow");
                        continue;
                    }

                    var wh = ReadImageSize(full);
                    string? hash = null;
                    try { hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))); } catch { /* 读哈希失败不阻塞 */ }

                    img = new ImageInfo
                    {
                        RelativePath = rel,
                        FileName = output.FileName,
                        Extension = Path.GetExtension(output.FileName),
                        FileSize = new FileInfo(full).Length,
                        Width = wh?.Item1 ?? 0,
                        Height = wh?.Item2 ?? 0,
                        Hash = hash,
                        WorkflowId = workflowId,
                        // 对象导航引用关联提示词：新建 ImagePrompt 尚未落库时 Id=0，
                        // 写 PromptId=0 会触发外键约束失败；EF 会按依赖序先插 Prompt 再插 Image
                        Prompt = prompt,
                        CreatedAt = File.GetLastWriteTimeUtc(full),
                        ImportedAt = DateTime.UtcNow,
                    };
                    db.ImageMetadata.Add(img);
                    await db.SaveChangesAsync(); // 保存 img（若新建 prompt 则同批插入）
                    _log.Info(string.Format(Localizer.Instance?["OutputAddedToGallery"] ?? "", rel, img.Id, workflowId), "Workflow");
                    // 生成缩略图文件（与图库同步同一命名规则），图库列表才能显示缩略图
                    try
                    {
                        await _imageSync.GenerateThumbnailAsync(full, rel, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(string.Format(Localizer.Instance?["ThumbnailGenFailed"] ?? "", rel), "Workflow", ex);
                    }
                }

                // 回填关联（同一 DbContext 内 Update，保证 ImageInfoId 落库）
                if (output.ImageInfoId != img.Id)
                {
                    output.ImageInfoId = img.Id;
                    db.JobOutputs.Update(output);
                    await db.SaveChangesAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(Localizer.Instance?["AutoImportFailed"] ?? "", jobId), "Workflow", ex);
        }
    }

    /// <summary>把 JSON 字符串解析为 JsonElement（供元数据提取器使用；失败返回 null）。</summary>
    private static JsonElement? ParseJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>轻量读取 PNG / WebP 图片尺寸（服务层后台线程避免解码整图；失败返回 null）。</summary>
    private static (int W, int H)? ReadImageSize(string path)
    {
        try
        {
            var bytes = new byte[64];
            using var fs = File.OpenRead(path);
            var read = fs.Read(bytes, 0, bytes.Length);
            if (read < 8) return null;

            // PNG：IHDR 第 16-23 字节为宽高（大端 uint32）
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 && read >= 24)
            {
                var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w, h);
            }

            // WebP：RIFF....WEBP，VP8/VP8L/VP8X 尺寸在不同偏移
            if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
                && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P' && read >= 30)
            {
                if (bytes[12] == 'V' && bytes[13] == 'P' && bytes[14] == '8' && bytes[15] == ' ')
                    return ((bytes[26] | (bytes[27] << 8)), (bytes[28] | (bytes[29] << 8))); // VP8 关键帧：26-29 为宽高(小端14bit)
                if (bytes[12] == 'V' && bytes[13] == 'P' && bytes[14] == '8' && bytes[15] == 'L')
                    return ((bytes[21] | ((bytes[22] & 0x0F) << 8)) & 0x3FFF,
                            (((bytes[22] & 0xF0) >> 4) | (bytes[23] << 4) | ((bytes[24] & 0x03) << 12)) & 0x3FFF);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>轻量更新 Job 字段（供 WS 回调使用，独立 DbContext 避免并发冲突）。</summary>
    private async void UpdateJobAsync(int jobId, Action<WorkflowJob> update)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var job = await db.WorkflowJobs.FirstOrDefaultAsync(x => x.Id == jobId && !x.IsDeleted);
            if (job == null) return;
            // 终态保护：job 已 completed/failed 时，迟到的 running/failed 事件（WS 消息乱序 / async void 竞态）
            // 一律不再覆盖终态，防止"执行完成后状态被改回排队中/执行中"
            if (job.Status is "completed" or "failed") return;
            update(job);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(Localizer.Instance?["UpdateJobFailed"] ?? "", jobId), "Workflow", ex);
        }
    }

    /// <summary>获取工作流执行历史（未删除，时间倒序，含输出）。</summary>
    public Task<List<WorkflowJob>> GetWorkflowJobsAsync(int workflowId)
    {
        return _jobRepo.GetByWorkflowAsync(workflowId);
    }

    /// <summary>
    /// 删除"图片提取"工作流：逻辑删除（Workflows 表 Source="image" 记录 + 关联 ImagePrompt，保留图片）。
    /// 图片详情仍可读取工作流/提示词数据（含归档角标）。无 ComfyUI 远端文件。
    /// </summary>
    public async Task<bool> DeleteImageWorkflowAsync(int imageWorkflowId)
    {
        _log.Info(string.Format(Localizer.Instance?["DeleteImageWorkflowStart"] ?? "", imageWorkflowId), "Workflow");
        using var db = await _dbFactory.CreateDbContextAsync();
        var workflow = await db.Workflows
            .Include(x => x.Prompts)
            .FirstOrDefaultAsync(x => x.Id == imageWorkflowId && (x.Source == "image" || x.Source == "video") && !x.IsDeleted);
        if (workflow == null)
        {
            _log.Warn(string.Format(Localizer.Instance?["ImageWorkflowMissing"] ?? "", imageWorkflowId), "Workflow");
            return false;
        }

        workflow.IsDeleted = true;
        foreach (var prompt in workflow.Prompts)
            prompt.IsDeleted = true;
        await db.SaveChangesAsync();
        // 标签关联物理清除（标签池与图库共用）
        await db.WorkflowTags.Where(x => x.WorkflowId == imageWorkflowId).ExecuteDeleteAsync();
        _log.Info(string.Format(Localizer.Instance?["ImageWorkflowDeleted"] ?? "", workflow.Name, workflow.Id), "Workflow");
        return true;
    }

    /// <summary>直接提交一段 API 格式工作流（不建执行记录、不跟踪进度）。兼容旧调用。</summary>
    public async Task<SubmitWorkflowResponse> SubmitWorkflowAsync(string promptJson)
    {
        if (!WorkflowJsonConverter.IsApiFormat(promptJson))
        {
            var objectInfo = await _client.GetObjectInfoAsync();
            var converted = WorkflowJsonConverter.ConvertToApi(promptJson, objectInfo);
            if (converted == null)
                return new SubmitWorkflowResponse { Success = false, ErrorMessage = Localizer.Instance?["ConvertFailedShort"] ?? "" };
            promptJson = converted;
        }
        return await _client.SubmitWorkflowAsync(promptJson, Guid.NewGuid().ToString("D"));
    }

    #endregion

    #region Workflow 删除

    /// <summary>
    /// 删除工作流：数据库逻辑删除（工作流 + 输入 + 执行任务 + 输出，保留图片），
    /// 并同步删除 ComfyUI 侧已保存的文件（DELETE /api/userdata/workflows/{file}，失败不阻塞）。
    /// </summary>
    public async Task DeleteWorkflowAsync(Workflow workflow)
    {
        _log.Info(string.Format(Localizer.Instance?["DeleteWorkflowStart"] ?? "", workflow.Name, workflow.Id, workflow.SourcePath ?? "-"), "Workflow");

        // 1) 数据库逻辑删除（含依赖数据）
        var deleted = await _workflowRepo.SoftDeleteAsync(workflow.Id);
        if (!deleted)
        {
            _log.Warn(string.Format(Localizer.Instance?["WorkflowMissingSkip"] ?? "", workflow.Id), "Workflow");
            return;
        }
        _log.Info(string.Format(Localizer.Instance?["WorkflowDeletedLog"] ?? "", workflow.Name), "Workflow");

        // 2) ComfyUI 侧物理删除（本地新建无 SourcePath 则跳过）
        if (!string.IsNullOrEmpty(workflow.SourcePath))
        {
            var ok = await _client.DeleteUserDataFileAsync(workflow.SourcePath);
            if (!ok)
                _log.Warn(string.Format(Localizer.Instance?["ComfyFileDeleteFailed"] ?? "", workflow.SourcePath), "Workflow");
        }
        else
        {
            _log.Debug(string.Format(Localizer.Instance?["NoSourcePathSkip"] ?? "", workflow.Name), "Workflow");
        }
    }

    #endregion
}
