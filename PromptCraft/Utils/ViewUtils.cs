using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using PromptCraft.Service.AssetGen;
using PromptCraft.Service.Documents;
using PromptCraft.Service.Inference;
using PromptCraft.Service.Inference.Reverse;
using PromptCraft.Service.Inference.Novel;
using PromptCraft.Service.PromptPlaza;
using PromptCraft.ViewModels;
using PromptCraft.ViewModels.ComfyUI;
using PromptCraft.ViewModels.Folders;
using PromptCraft.ViewModels.PromptLibrary;
using PromptCraft.ViewModels.System;
using PromptCraft.Views;
using PromptCraft.Views.ComfyUI;
using PromptCraft.Views.Folders;
using PromptCraft.Views.PromptLibrary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;

namespace PromptCraft.Utils
{
    public static class ViewUtils
    {
        public static ServiceCollection InitViewServices(this ServiceCollection collection)
        {
            collection.AddScoped<AppViewModel>();
            var service = collection.BuildServiceProvider();
            var view = service.GetService<IBaseViewService>()!;
            view.AddView<MainWindow, MainWindowModel>(collection, "Main");
            view.AddView<Setting, SettingModel>(collection);
            //view.AddView<Test, ViewModels.Test.TestViewModel>(collection);
            view.AddView<ExpandView, ExpandViewModel>(collection);
            view.AddView<ImitationView, ImitationViewModel>(collection);
            view.AddView<NovelToPromptView, NovelToPromptViewModel>(collection);
            view.AddView<ReverseCaptionView, ReverseCaptionViewModel>(collection);
            view.AddView<PromptEngineeringView, PromptEngineeringViewModel>(collection);
            view.AddView<TrainDatasetsView, TrainDatasetsViewModel>(collection);
            view.AddView<PromptPlazaView, PromptPlazaViewModel>(collection);
            view.AddView<ReverseBatchSaveView, ReverseBatchSaveModel>(collection, "ReverseBatchSave", false);
            view.AddView<LogView, LogViewModel>(collection);
            view.AddView<ComfyUIView, ComfyUIViewModel>(collection);
            view.AddView<CustomThemColor, CustomThemColorModel>(collection, "CustomThemColor", false);
            view.AddView<ComfyGallery, ComfyGalleryModel>(collection);
            view.AddView<ComfyWorkflowView, ComfyWorkflowModel>(collection);
            view.AddView<ImageDetailView, ImageDetailModel>(collection, "ImageDetail", false);
            view.AddView<WorkflowDetailView, WorkflowDetailModel>(collection, "WorkflowDetail", false);
            view.AddView<ThumbnailPickerView, ThumbnailPickerModel>(collection, "ThumbnailPicker", false);
            view.AddView<ImageCompareView, ImageCompareModel>(collection, "ImageCompare", false);
            view.AddView<WorkflowParamsView, WorkflowParamsModel>(collection, "WorkflowParams", false);
            view.AddView<TagManagerView, TagManagerModel>(collection, "TagManager", false);
            view.AddView<BatchTagView, BatchTagModel>(collection, "BatchTag", false);
            // 提示词库页面 + 编辑对话框（瞬时，随弹窗创建）
            view.AddView<PromptLibraryView, PromptLibraryModel>(collection);
            view.AddView<PromptEditDialogView, PromptEditModel>(collection, "PromptEdit", false);
            view.AddView<PromptBatchTagView, PromptBatchTagModel>(collection, "PromptBatchTag", false);
            // 统一文件夹管理页（工作流/提示词/图库）
            view.AddView<FolderManageView, FolderManageViewModel>(collection);
            // 提示词工程「新建/编辑」与「JSON 导入」对话框（瞬时，随弹窗创建，与词库 PromptEdit 同构）
            view.AddView<PeEditorView, PeEditorModel>(collection, "PeEditor", false);
            view.AddView<PeImportView, PeImportModel>(collection, "PeImport", false);
            return collection;
        }

        /// <summary>
        /// 注册 ComfyUI 服务
        /// </summary>
        public static ServiceCollection AddComfyUIServices(this ServiceCollection collection)
        {
            // ComfySettings 单例（从 Config 读取；数据库路径固定，缩略图目录走工作空间 cache）
            collection.AddSingleton(sp =>
            {
                var cfg = StaticData.AppInitData.Config;
                var ws = sp.GetService<IWorkspaceService>();
                return new ComfySettings
                {
                    ComfyApiUrl = cfg?.ComfyApiUrl ?? "http://127.0.0.1:8188",
                    ComfyOutputDir = cfg?.ComfyOutputDir ?? string.Empty,
                    ThumbMaxDimension = cfg?.ThumbMaxDimension ?? 300,
                    ThumbQuality = cfg?.ThumbQuality ?? 80,
                    ThumbDir = ws != null
                        ? Path.Combine(ws.CacheDir, "thumbnails")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "PromptCraft", "comfy_data", "thumbnails"),
                };
            });

            // DbContext 工厂（业务库固定 %LOCALAPPDATA%/PromptCraft/comfy_data/comfyui.db）
            collection.AddDbContextFactory<ComfyDbContext>((sp, opts) =>
            {
                // SQLite 不会自动创建父目录：先确保 comfy_data 存在，否则连接报 "SQLite Error 14: unable to open database file"
                var dbDir = Path.GetDirectoryName(ComfySettings.DbPath);
                if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
                opts.UseSqlite($"Data Source={ComfySettings.DbPath}");
            });

            // T1.1 提示词库已并入 comfyui.db（单库）；不再注册独立库工厂
            collection.AddSingleton<IPromptRepository, PromptLibraryRepository>();
            // T1.2 词库服务层（prompt/tag CRUD + 变更事件）
            collection.AddSingleton<IPromptLibraryService, PromptLibraryService>();
            // 统一单级文件夹服务（工作流/提示词/图库）
            collection.AddSingleton<IFolderService, FolderService>();
            // 跨页共享"文件夹资产剪贴板"（菜单管理复制 ↔ 提示词库复制 ↔ 菜单管理粘贴）
            collection.AddSingleton<Utils.IFolderClipboardService, Utils.FolderClipboardService>();

            // 服务
            collection.AddSingleton<IImageSyncService, ImageSyncService>();
            collection.AddSingleton<IComfyUIClient, ComfyUIClient>();
            collection.AddSingleton<IComfyUIWebSocketHub, ComfyUIWebSocketHub>(); // 全局长连接 WS 会话（启动即连 + 断线重连）
            collection.AddSingleton<IComfyUIService, ComfyUIService>();

            // 知识库文档深度解析（v2：docx/PDF 含扫描件 OCR，离线 SimdPaddleOCR；懒加载模型，单例复用）
            collection.AddSingleton<KnowledgeDocumentReader>();

            // 资产生图（v2 #4：半自动模板制，环境自适应 + fail-fast；依赖 IComfyUIClient/IAppSettingsRepository/ComfySettings）
            collection.AddSingleton<AssetImageGenerationService>();

            collection.AddSingleton<IWorkflowRepository, WorkflowRepository>();
            collection.AddSingleton<IImageMetadataRepository, ImageMetadataRepository>();
            collection.AddSingleton<IImageStatusRepository, ImageStatusRepository>();
            collection.AddSingleton<ITagRepository, TagRepository>();
            collection.AddSingleton<IBlacklistRepository, BlacklistRepository>();
            collection.AddSingleton<IWorkflowInputRepository, WorkflowInputRepository>();
            collection.AddSingleton<IJobRepository, JobRepository>();
            collection.AddSingleton<IJobOutputRepository, JobOutputRepository>();
            collection.AddSingleton<IWorkflowParamsRepository, WorkflowParamsRepository>();
            collection.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();
            collection.AddSingleton<IExpandSettingsStore, ExpandSettingsStore>();

            // T0.1 工作空间（PromptMaster 迁移）：单例，启动时 InitializeAsync 建子目录
            collection.AddSingleton<IWorkspaceService, WorkspaceService>();

            // T0.7 AI 提供商管理
            collection.AddSingleton<IProviderService, ProviderService>();
            collection.AddSingleton<IExpandService, ExpandService>();
            collection.AddSingleton<IImitationService, ImitationService>();
            collection.AddSingleton<INovelToPromptService, NovelToPromptService>();
            collection.AddSingleton<ITranslateService, TranslateService>();
            collection.AddSingleton<IReverseCaptionService, ReverseCaptionService>();

            // 模型训练数据集服务（磁盘文件制；批量打标复用反推服务 + train 工程）
            collection.AddSingleton(sp =>
            {
                var workspace = sp.GetRequiredService<IWorkspaceService>();
                var reverse = sp.GetRequiredService<IReverseCaptionService>();
                var peService = new PmPromptEngineeringService(
                    workspaceRoot: string.IsNullOrEmpty(workspace.Root) ? null : workspace.Root,
                    dbFactory: sp.GetRequiredService<IDbContextFactory<ComfyDbContext>>());
                return new DatasetService(workspace, reverse, peService, sp.GetService<IBaseLogService>());
            });

            // 提示词广场数据源（预留云端接口，未配置时返回空）
            collection.AddSingleton<IPromptPlazaSource, CloudPromptPlazaSource>();

            // 配置 DB（Singleton：设置 DB 操作少，不需要 scope）
            collection.AddDbContext<SettingsDbContext>(opts =>
            {
                opts.UseSqlite($"Data Source={Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PromptCraft", "settings.db")}");
            }, ServiceLifetime.Singleton);
            collection.AddSingleton<ConfigRepository>();
            return collection;
        }
    }
}