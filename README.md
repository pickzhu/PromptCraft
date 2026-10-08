# PromptCraft

> AI 提示词创作工坊 —— 以 ComfyUI 为核心的跨平台桌面应用，集成提示词工程、小说转提示词、资产生图、图库管理、训练数据集与本地化离线能力。

**PromptCraft** 是一个基于 **Avalonia 12** 的跨平台桌面应用（Windows / Linux /macOS，另含 WASM / Android /iOS 入口）。它把「提示词创作」与「ComfyUI 生图」串成一条完整流水线：从小说、知识库、参考图，到提示词工程与批量标注，再到工作流执行与资产沉淀，全部在一个界面里闭环。



* **技术基线**：.NET 10（net10.0）/ C# latest / Avalonia 12.1 / SukiUI 7 / EFCore 9 + SQLite

* **解决方案**：`PromptCraft.sln`（15 个工程 + 1 个聚合构建入口）

* **许可证**：MIT



***

## 目录



* [功能特性](#功能特性)

* [技术栈](#技术栈)

* [项目架构](#项目架构)

* [目录结构](#目录结构)

* [工程说明](#工程说明)

* [核心文件介绍](#核心文件介绍)

* [数据存储](#数据存储)

* [构建与运行](#构建与运行)

* [测试](#测试)

* [开发约定](#开发约定)

* [路线图](#路线图)

* [参与贡献](#参与贡献)

* [许可证](#许可证)



***

## 功能特性

### 1. 小说 → 提示词流水线（NovelToPrompt）

把小说章节自动加工为可执行的分镜提示词，七阶段流水线编排（`NovelToPromptService`）：



| 阶段    | 内容                                                                                   |
| ----- | ------------------------------------------------------------------------------------ |
| S1–S4 | 单文本阶段走大模型：概述、角色、场景、细节（知识库启用时注入目录文本作参考）                                               |
| S5    | 资产扫描：先由 LLM 提取资产需求表 → 扫描资产目录比对 → 报告缺失资产，缺口提示词分批输出（含截断续写 / 缺失补漏）                      |
| S6    | 镜头规划门：按场次逐场生成镜头 JSON，长场自动分批（≤5 镜 / 批）拼装，杜绝截断                                         |
| S7    | 逐镜提示词：H3 中文直投 / MiniMax 六段式（full\_reference）/ 连续剧情导演台 / Seedance 2.0，单条截断自动续写，逐场合并输出 |



* 输出语言：中文 / English 可选，全部阶段强制使用所选语言

* 模型：页面已选模型优先，回落扩写默认；温度 0.7 /top\_p 0.9，输出经思考标签清洗

* 配套 UI：`NovelToPromptView`（世界观 / TAG 处理 / 分镜规划 / 提示词生成 / 资产缺口）

### 2. 知识库深度解析（离线 OCR + PDF）

`KnowledgeDocumentReader` 让知识库不再只吃纯文本：



* **格式**：txt /docx/pdf / 图片

* **PDF**：Docnet.Core（PDFium）渲染页面为位图，扫描件也能读

* **OCR**：Sdcb.SimdPaddleOCR（PP-OCRv6 中文模型，内嵌模型、纯托管 SIMD 推理、Apache-2.0、**完全离线、无云服务**）

* 单文档 80k 字符上限，(路径，mtime) 双重缓存

### 3. 资产生图（环境自适应模板）

`AssetImageGenerationService` 面向「缺口提示词 → 批量资产图」：



* **文生图 / 图生图双骨架**模板，不绑定具体模型（适配用户本地任意 checkpoint / LoRA）

* **环境自适应**：提交前 `/object_info` 探测 ComfyUI 可用模型 + dry-run fail-fast

* **人工验收**：生成资产进入 `pending` 目录，人工确认后再归位，避免脏数据入库

* 5 分钟轮询、SD1.5 档 768 系默认出图参数、默认模型键 `asset_gen.default_ckpt`

> 注：资产生图模板服务已内置并通过测试；UI 触发按钮当前处于隐藏状态（等待 ComfyUI 模型探测解析修复后开放）。

### 4. 反向标注 / 批量打标（ReverseCaption）

多模型反向提示词与描述生成：



* **多模型提示词工程**：Danbooru、JoyCaption、Natural Language、Descriptive、AltProse、Anima3、ToriiGate、MiniMax 等风格

* 批量处理、标签行清洗、输出长度 / 能力约束、思考标签启发式

* 双语输出、百度翻译互译（`TranslateService`）

### 5. ComfyUI 图库（ComfyGallery）



* **图片同步**：扫描 ComfyUI 输出目录（png/jpg/jpeg/webp/bmp，含子目录）→ 解析 PNG tEXt/iTXt/zTXt、WebP EXIF 元数据 → 无 ComfyUI 元数据 / 无工作流 ID 的图跳过

* **元数据提取**：正 / 负提示词、模型、Seed、Steps、CFG、Sampler、Scheduler、宽高、LoRA、全量节点快照；工作流只存 "结构版"（参数剔除 + GZip），详情时重组回完整工作流

* **去重**：工作流按 ComfyUI 顶级 UUID 唯一；提示词按 (WorkflowId, PromptHash) 唯一；存量回填 + 缩略图命名迁移

* **画廊 UI**：缩略图流式分页、搜索（文件名 / 标签 / 哈希）、筛选（收藏 / NSFW / 标签多选）、批量删除 / 批量打标、标签抽屉管理

* **图片详情**：大图、收藏 / NSFW / 评分 / 备注、标签、提取提示词与参数、删除

* **图片对比**：拖拽分割线对比，三层 diff —— 参数字段 diff、正 / 负提示词字符级 diff（TextDiff）、工作流节点级 diff（WorkflowDiffer）

### 6. ComfyUI 工作流管理



* **同步**：从 ComfyUI 拉取用户工作流文件（`/api/userdata`），按 SourcePath upsert

* **编辑**：AvaloniaEdit + TextMate 语法高亮（深浅主题自动切换）、预览图挑选

* **执行**：UI→API 格式自动转换（与 ComfyUI 前端 convertWorkflowToAPI 一致：/object\_info 字段序映射、注释节点过滤、widgets 白名单）→ POST /api/prompt（固定 client\_id + extra\_pnginfo 使 PNG 携带工作流）→ 后台跟踪

* **执行跟踪**：WebSocket 实时消息（start/executing/progress/executed/success/error/interrupted）+ 2 秒轮询 /history 兜底（WS 断线时轮询至终态）→ 终态落库 → **输出图片自动加入图库**

* **参数配置**：按节点 / 字段配置固定值或随机范围（seed 等数值字段支持随机），保存为每工作流一份的 WorkflowParams

* **执行历史**：状态 / 进度 / 节点日志 / 输出图，可打开详情 / 对比、删除单条 / 清空

### 7. 提示词库（PromptLibrary）



* 本地提示词库：卡片浏览、搜索、Tag 多选筛选

* 批量打标（PromptBatchTag）、标签管理抽屉、编辑对话框

* 大图预览、封面拖拽排序、库内导入 / 整理

### 8. 提示词工程与 PE 编辑器



* **PromptEngineering**：工程化提示词构建

* **PE 编辑器**（PeEditor / PeImport）：编辑 / 导入工作台

### 9. 训练数据集



* **TrainDatasets**：数据集列表 + 详情（TrainDatasetDetail），从路径批量加图（`DatasetService.AddImagesFromPaths`）

* 面向 ComfyUI / SD 训练数据组织的辅助管理

### 10. 扩写 / 模仿 / 提示词广场



* **扩写（Expand）**：规则化扩写（ExpandRules）+ MiniMax 场景组装（六段式 / 连续剧情 / 导演台），复用同一 MiniMaxAssembler

* **模仿（Imitation）**：参考示例生成模仿提示词

* **提示词广场（PromptPlaza）**：云端提示词资源浏览 / 获取（CloudPromptPlazaSource）

### 11. 应用外壳与设置



* SukiUI 主窗口：侧边菜单导航、托盘图标、Toast / 对话框宿主

* **设置页**：亮 / 暗主题、背景风格（Flat/Gradient/GradientSoft/GradientDarker/Bubble）、背景动画 / 过渡开关、主题色（内置色板 + 自定义）、语言 zh-CN/en-US 即时切换、ComfyUI 连接配置（地址 / 输出目录 / 数据目录 / 连接测试）、日志级别

* 配置持久化：`%APPDATA%/PromptCraft/settings.db`（SQLite 单行表），旧 JSON 配置自动迁移

### 12. 日志系统



* 6 级日志（Trace/Debug/Info/Warn/Error/Fatal）+ 分类 + 异常格式化

* 每日轮转 `logs/log-yyyyMMdd.log`（保留 7 天）、Error/Fatal 累积 `errors.log`

* 非阻塞写入（Channel 队列 + 内存缓冲 + LogWritten 事件）、全局异常捕获（AppDomain + TaskScheduler）

* 日志页：实时倒序、级别 / 关键字过滤、错误红色高亮、导出错误、打开目录

* Windows 原生崩溃 → 进程内 SEH 过滤器 MiniDumpWriteDump，硬崩溃 → WER LocalDumps 兜底（`CrashGuard`）

### 13. 媒体能力



* 图片 / 视频内嵌预览（LibVLCSharp 软渲染，素材卡内播放，对齐 PromptMaster `<video>/<audio>`）

* 视频抽帧（VideoFrameGrabber / VideoFrameExtractor）

* 剪贴板 / 文件夹选择 / 多选下拉 / 瀑布流面板等通用控件



***

## 技术栈



| 类别         | 技术 / 包                                                                             | 版本                             |
| ---------- | ---------------------------------------------------------------------------------- | ------------------------------ |
| 运行时        | .NET / C#                                                                          | **net10.0**，LangVersion latest |
| UI 框架      | Avalonia（Skia / Fluent / Inter / Android / iOS / Browser / ColorPicker / DataGrid） | 12.1.2                         |
| UI 主题      | SukiUI（窗口 / 侧边菜单 / Toast / 对话框 / MessageBox）                                       | 7.0.2-nightly20261002.475      |
| MVVM       | CommunityToolkit.Mvvm（源生成器）                                                        | 8.4.0                          |
| DI         | Microsoft.Extensions.DependencyInjection                                           | 10.0.12                        |
| ORM        | Microsoft.EntityFrameworkCore.Sqlite                                               | 9.0.4                          |
| 停靠布局       | Dock.Avalonia/ Dock.Model.Avalonia（PromptCraft.Dock 定制主题）                          | 12.1.0.6                       |
| 代码编辑器      | Avalonia.AvaloniaEdit + AvaloniaEdit.TextMate                                      | 12.0.0                         |
| Markdown   | AvaloniaHotMarkdown + PromptCraft.UILib 内置渲染库                                      | 0.2.5                          |
| 图标         | Material.Icons.Avalonia                                                            | 3.0.2                          |
| 图像         | SkiaSharp                                                                          | 3.119.4                        |
| 图表         | LiveChartsCore.SkiaSharpView.Avalonia                                              | 2.0.0-rc5.4                    |
| 媒体         | LibVLCSharp / LibVLCSharp.Avalonia / VideoLAN.LibVLC.Windows                       | 3.10.1 / 3.0.24                |
| **离线 OCR** | **Sdcb.SimdPaddleOCR + ChineseV6Tiny**（PP-OCRv6，Apache-2.0）                        | 1.4.2 / 1.0.0                  |
| **PDF 渲染** | **Docnet.Core**（PDFium，MIT）                                                        | 2.6.0                          |
| 本地化        | Ke.Bee.Localization（JSON Provider，仓库内嵌）                                            | 0.1.1                          |
| JSON       | System.Text.Json                                                                   | 10.0.2                         |
| 混淆         | Obfuscar.MsBuild                                                                   | 2.2.50                         |
| SVG        | Svg.Controls.Avalonia                                                              | 12.0.0.17                      |
| 测试         | MSTest                                                                             | —                              |
| 包管理        | Central Package Management（Directory.Packages.props 统一版本）                          | —                              |



***

## 项目架构

分层清晰：**Shells（平台入口）→ 主应用（UI + 业务）→ 服务层 → 数据层 → 接口层 → 基础库**。



```
PromptCraft.Desktop / .Browser / .Android / .iOS    平台入口（net10.0 / -browser / -android / -ios）
        └── PromptCraft（主应用：ViewModels / Views / Controls / Styles / Assets）
                ├── PromptCraft.Service   服务实现（ComfyUI、推理流水线、OCR、资产生图、日志…）
                ├── PromptCraft.Data      EFCore + SQLite（settings.db / comfyui.db）
                ├── PromptCraft.Models    领域模型（ComfyUI、Inference、配置、提示词库…）
                ├── PromptCraft.Interfaces 接口层（服务/仓储/事件/日志/视图/页面…）
                ├── PromptCraft.Consts    常量（配置名 / 事件名）
                ├── BaseClassLib          基础类库（MVVM 基类、主题、语言、DI 扩展）
                ├── PromptCraft.UILib     Markdown 渲染库
                ├── PromptCraft.Dock      停靠布局 SukiUI 主题定制
                └── Ke.Bee.Localization   JSON 本地化库
```

**架构模式核心约定**：



* **MVVM**：CommunityToolkit.Mvvm 源生成器（`[ObservableProperty]` / `[RelayCommand]`）；`ViewModelBase : ModelBase(ObservableValidator)`；XAML 使用 `x:DataType` + 编译绑定（`AvaloniaUseCompiledBindingsByDefault=true`）

* **视图定位**：`ViewService` 维护 VM→View 映射表，`ViewLocator`（IDataTemplate）按 VM 实例查表创建视图并**按实例缓存**

* **DI**：Microsoft.Extensions.DependencyInjection，三处注册入口（平台无关基础服务 / 视图与 VM / ComfyUI 全家桶：Client、WS Hub、Service、9 个 Repository、2 个 DbContext）

* **接口 + 实现 + DI**：所有基础服务走 `IBaseXxx` 接口 → `PromptCraft.Service` 实现 → DI 注册

* **事件总线**：`NoticeService`（IBaseNotice）—— 字符串事件名（EventNameConst）发布 / 订阅，线程安全，处理器异常自动记 Error

* **仓储模式**：Repository 注入 `IDbContextFactory<ComfyDbContext>`，每操作独立 DbContext（防追踪脏读），只读查询一律 `AsNoTracking`

* **静态引导点**：DI 就绪前使用静态实例（LogService.Instance/ ComfyUIWebSocketHub.Instance/ AppInitData.Config）

* **本地化**：Ke.Bee.Localization（zh-CN / en-US JSON），XAML `{i18n:Localize}`，VM 注入 ILocalizer；语言切换经事件总线广播

* **主题**：SukiTheme 单例，深 / 浅色、背景风格 / 动画 / 过渡、主题色由启动时按配置应用，退出时回写

**启动流程（Desktop）**：`Program.Main`（日志引导 + 清理）→ `App.Initialize`（全局异常处理 → 建配置库 → 读配置 → 应用日志级别 → 旧 JSON 配置迁移 → 加载 XAML）→ `OnFrameworkInitializationCompleted`（构建 DI → 升级 Comfy 库 schema → 注册视图定位器 → 创建主窗口 → 启动 ComfyUI WebSocket 长连接）。



***

## 目录结构



```
promptcraft/
├── PromptCraft.sln                     # 解决方案（15 个工程）
├── Directory.Packages.props            # 中央包管理（CPM），统一 NuGet 版本（AvaloniaVersion 等）
├── build_all.csproj                    # 一键聚合构建入口（ProjectReference 聚合）
├── README.md / README.en.md            # 项目文档（本文件）/ 英文版
├── PROJECT_ANALYSIS.md                 # AI 生成的项目分析报告（跨会话上下文恢复）
├── LOGGING.md                          # 日志系统设计文档（含踩坑记录）
├── AGENTS.md / .editorconfig / .gitattributes / .gitignore / LICENSE
├── .mcp.json / create_mcp.py           # MCP 配置与生成脚本
├── docs/
│   └── v2-迭代待办.md                    # v2 迭代待办清单
│
├── BaseClassLib/                       # 基础类库（无业务）
│   ├── ModelBase.cs                    # ObservableValidator 基类（DisplayName/Icon/Index/SideMenu）
│   ├── Extends/                        # LanguageExtend、ServiceProviderExtend、SukiMessageBox…FactoryExtend
│   ├── Manager/ViewManger.cs
│   └── Models/                         # Config、AddThemeModel、LanguageEnum、ListItem<T>
│
├── PromptCraft.Consts/                 # 常量层（零依赖）
│   ├── Config/ConfigNameConst.cs       # 配置名常量
│   └── Event/EventNameConst.cs         # 事件名常量
│
├── PromptCraft.Interfaces/             # 接口层（服务/仓储/事件/日志/视图/页面/剪贴板…）
│   ├── IBaseNotice.cs / IBaseLogService.cs / IBaseViewService.cs / IBasePageService.cs
│   ├── IProviderService.cs / IComfyUIService.cs / IComfyUIClient.cs / IComfyUIWebSocketHub.cs
│   ├── INovelToPromptService.cs / IReverseCaptionService.cs / IExpandService.cs / IImitationService.cs
│   ├── IPromptLibraryService.cs / IPromptPlazaSource.cs / ITranslateService.cs / IWorkspaceService.cs
│   ├── 仓储接口：IAppSettingsRepository / IImageMetadataRepository / IWorkflowRepository / ITagRepository…
│   └── LogLevel.cs / LogEntry.cs
│
├── PromptCraft.Models/                 # 领域模型（POCO/实体）
│   ├── AppConfig.cs / AppSetting.cs / ProviderConfig.cs / PlazaPrompt.cs / PromptLibraryEntities.cs
│   ├── ComfyUI/                        # ComfyApiModels（/object_info 等 API DTO）、ComfyModels、TagPalette
│   ├── Inference/                      # ChatCompletionModels、ReverseCaptionModels、ExpandModels、ImitationModels
│   │   └── Novel/NovelModels.cs
│   └── LogLevel.cs
│
├── PromptCraft.Data/                   # 数据层（EFCore + SQLite）
│   ├── SettingsDbContext.cs            # 配置库（%APPDATA%/PromptCraft/settings.db）
│   ├── ComfyDbContext.cs               # ComfyUI 业务库（13 张表，Fluent 配置）
│   ├── ComfyDbMigrator.cs              # 手工 schema 升级（PRAGMA 补列/建表，幂等）
│   ├── ConfigRepository.cs             # 配置仓库（静态/实例，JSON↔DB 迁移）
│   ├── ComfyRepositories.cs            # 9 个 Repository（IDbContextFactory + 独立上下文）
│   └── ComfyImageMetadata.cs / ComfyVideoMetadata.cs
│
├── PromptCraft.Service/                # 服务实现层（核心业务）
│   ├── ConfigService.cs                # DI 注册扩展 + 基础服务
│   ├── ProviderService.cs              # 模型提供方（Ollama/OpenAI 兼容…）
│   ├── OllamaApiHelper.cs / OpenAiHttpHelper.cs   # LLM API 客户端
│   ├── ComfyUIService.cs / ComfyUIClient / ComfyUIWebSocketHub.cs   # ComfyUI 连接/执行
│   ├── ComfyWorkflowNormalizer.cs / WorkflowDiffer.cs / TextDiff.cs
│   ├── KnowledgeDocumentReader.cs      # 知识库深度解析（txt/docx/pdf + 离线 OCR）
│   ├── AssetGen/AssetImageGenerationService.cs   # 资产生图（T2I/I2I 环境自适应模板）
│   ├── Inference/                      # 推理流水线
│   │   ├── Novel/                      # NovelToPromptService、H3PromptBuilder、SeedancePromptBlocks…
│   │   ├── Reverse/                    # Danbooru/JoyCaption/Anima3/NaturalLanguage/Descriptive/AltProse/
│   │   │                               #   ToriiGate/MiniMax 等提示词工程
│   │   └── Minimax/                    # MiniMaxAssembler、MediaVision、ExpandMedia
│   ├── PromptPlaza/                    # 提示词广场（云资源）
│   ├── DatasetService.cs / FolderService.cs / PromptLibraryService.cs / WorkspaceService.cs
│   ├── ExpandService.cs / ImitationService.cs / ReverseCaptionService.cs
│   ├── TranslateService.cs             # 百度翻译
│   ├── LogService.cs / Diagnostics/CrashGuard.cs
│   └── ClipboardService.cs / NoticeService.cs / ViewService.cs / PageService.cs
│
├── PromptCraft.UILib/                  # Markdown 渲染库
│   └── Markdown/                       # MarkdownRenderer(+Input/Utils)、MarkdownTextBlock、CodeBlock、
│                                       #   SyntaxHighlighting、AsyncImageLoader、Styles.axaml…
│
├── PromptCraft.Dock/                   # Dock.Avalonia SukiUI 主题定制（Document/Tool 停靠、标签条等）
│   └── Converters/                     # 8 个停靠相关转换器
│
├── PromptCraft/                        # ★ 主应用（UI 与业务）
│   ├── App.axaml(.cs)                  # 启动引导、主题/语言、托盘图标
│   ├── BaseModel/                      # ViewModelBase、PageBase
│   ├── Common/                         # StorageService、ViewLocator
│   ├── Controls/                       # CodeEditor、MediaVideoView、MultiSelectDropDown、TagChipTextBox、
│   │                                   #   PromptLibraryTagDrawer、WaterfallPanel…
│   ├── Converters/                     # BoolToEyeIconConverter 等
│   ├── DialogView/                     # CustomThemColor（自定义主题色）
│   ├── Service/                        # LibVlcProvider
│   ├── StaticData/AppInitData.cs       # 全局配置静态入口
│   ├── Utils/                          # ViewUtils（DI 注册）、FolderClipboardService、VideoFrameGrabber…
│   ├── ViewModels/
│   │   ├── AppViewModel.cs / MainViewModel.cs / CustomThemColorModel.cs
│   │   ├── ComfyUI/                    # ComfyGalleryModel、ComfyWorkflowModel、ImageDetailModel、
│   │   │                               #   WorkflowDetailModel、ImageCompareModel、ThumbnailPickerModel、
│   │   │                               #   WorkflowParamsModel、TagManagerModel、BatchTagModel…
│   │   ├── PromptLibrary/              # PromptLibraryModel、PromptEditModel、PromptBatchTagModel…
│   │   ├── System/                     # NovelToPromptViewModel、PromptEngineeringViewModel、PeEditorModel、
│   │   │                               #   ReverseCaptionViewModel、TrainDatasetsViewModel、
│   │   │                               #   TrainDatasetDetailViewModel、ComfyUIViewModel、ExpandViewModel、
│   │   │                               #   ImitationViewModel、PromptPlazaViewModel、SettingModel、LogViewModel、
│   │   │                               #   FolderManageViewModel…
│   │   └── Test/TestViewModel.cs
│   ├── Views/
│   │   ├── MainWindow.axaml            # SukiWindow + 侧边菜单导航宿主
│   │   ├── ComfyUI/                    # ComfyGallery、ComfyWorkflowView、ImageDetailView、WorkflowDetailView、
│   │   │                               #   ImageCompareView、ThumbnailPickerView、WorkflowParamsView、
│   │   │                               #   TagManagerView、BatchTagView…
│   │   ├── PromptLibrary/              # PromptLibraryView、PromptEditDialogView、PromptBigImageView…
│   │   ├── System/                     # NovelToPromptView、PromptEngineeringView、PeEditorView、PeImportView、
│   │   │                               #   ReverseCaptionView、TrainDatasetsView、TrainDatasetDetailView、
│   │   │                               #   ComfyUIView、ExpandView、ImitationView、PromptPlazaView、
│   │   │                               #   Setting.axaml、LogView.axaml、FolderManageView…
│   │   └── Test/
│   ├── Styles/                         # AppColors、CommonStyles、GlassCardStyles、TextStyles…
│   └── Assets/                         # 图标（logo.ico 7 档尺寸 / node.png）、i18n/zh-CN.json、en-US.json、
│                                       #   prompts/、workflow_placeholder.png
│
├── PromptCraft.Desktop/                # Windows 桌面入口（Program.cs、app.manifest、发布配置）
├── PromptCraft.Browser/                # WASM 入口（net10.0-browser）
├── PromptCraft.Android/                # Android 入口（net10.0-android）
├── PromptCraft.iOS/                    # iOS 入口（net10.0-ios）
├── PromptCraft.Test/                   # MSTest 测试工程（19 个测试文件）
└── Ke.Bee.Localization/                # JSON 本地化库（Avalonia，仓库内嵌）
```



***

## 工程说明



| 工程                     | 类型                   | 职责                                                                      |
| ---------------------- | -------------------- | ----------------------------------------------------------------------- |
| PromptCraft            | 应用（net10.0）          | 主应用：全部 UI（ViewModels / Views / Controls / Styles）与页面业务                  |
| PromptCraft.Desktop    | 可执行（net10.0）         | Windows 桌面入口：日志引导、顶层异常 Fatal、WinExe 发布                                  |
| PromptCraft.Browser    | 可执行（net10.0-browser） | WASM 入口（Avalonia.Browser）                                               |
| PromptCraft.Android    | 可执行（net10.0-android） | Android 入口（Avalonia.Android）                                            |
| PromptCraft.iOS        | 可执行（net10.0-ios）     | iOS 入口（Avalonia.iOS）                                                    |
| PromptCraft.Service    | 类库                   | 服务实现：ComfyUI 连接与执行、推理流水线、OCR、资产生图、日志、提示词库、数据集等                          |
| PromptCraft.Data       | 类库                   | 数据层：EFCore + SQLite（settings.db/comfyui.db）、9 个 Repository、手工 schema 迁移 |
| PromptCraft.Models     | 类库                   | 领域模型：ComfyUI API DTO、推理模型、配置、提示词库实体                                     |
| PromptCraft.Interfaces | 类库                   | 全部服务 / 仓储 / 事件 / 日志 / 视图 / 页面接口（IBaseXxx 命名约定）                          |
| PromptCraft.Consts     | 类库                   | 配置名 / 事件名常量（零依赖）                                                        |
| BaseClassLib           | 类库                   | 基础类库：MVVM 基类、主题 / 语言模型、DI 扩展、MessageBox 工厂                              |
| PromptCraft.UILib      | 类库                   | Markdown 渲染库（Markdig + TextMate/ColorCode 高亮 + 异步图片 + SVG）              |
| PromptCraft.Dock       | 类库                   | Dock.Avalonia SukiUI 主题定制（停靠控件模板与转换器）                                   |
| Ke.Bee.Localization    | 类库                   | Avalonia JSON 本地化库（仓库内嵌副本）                                              |
| PromptCraft.Test       | 测试（net10.0）          | MSTest：108 个测试                                                          |
| build\_all.csproj      | 聚合（net10.0）          | 一键构建全部项目的聚合入口（自身不编译代码）                                                  |



***

## 核心文件介绍



| 文件                                                               | 说明                                                       |
| ---------------------------------------------------------------- | -------------------------------------------------------- |
| `PromptCraft.Desktop/Program.cs`                                 | 桌面入口：日志引导 / 清理、顶层异常 Fatal、Avalonia 启动                    |
| `PromptCraft/App.axaml(.cs)`                                     | 应用启动：异常处理 → 建库 → 读配置 → DI → 视图定位 → WS 长连接；托盘图标 / 主题 / 语言 |
| `PromptCraft/Common/StorageService.cs`                           | 文件选择器、AppData / 文档路径、JSON 配置读写、文件名净化                     |
| `PromptCraft/Common/ViewLocator.cs`                              | VM→View 映射 + 视图缓存（IDataTemplate）                         |
| `PromptCraft/Utils/ViewUtils.cs`                                 | DI 注册：12 个 View+VM + ComfyUI 服务全家桶                       |
| `PromptCraft/StaticData/AppInitData.cs`                          | 全局配置静态入口（Config + 加载 / 保存）                               |
| `PromptCraft.Service/ConfigService.cs`                           | DI 注册扩展：InitPromptCraftServices（基础服务全家桶）                 |
| `PromptCraft.Service/ComfyUIService.cs`                          | 工作流同步 / 执行跟踪 / 删除 + 输出自动入库                               |
| `PromptCraft.Service/ComfyUIWebSocketHub.cs`                     | 应用级 WS 全局长连接（3 次重试 + 断线自动重连）                             |
| `PromptCraft.Service/ComfyWorkflowNormalizer.cs`                 | 工作流结构 / 参数分离与重组（去重核心）                                    |
| `PromptCraft.Service/ComfyImageMetadata.cs`                      | PNG/WebP 元数据读取、参数提取、GZip 编解码                             |
| `PromptCraft.Service/NovelToPromptService.cs`                    | 小说→提示词七阶段流水线编排（S1–S7）                                    |
| `PromptCraft.Service/KnowledgeDocumentReader.cs`                 | 知识库深度解析：txt/docx/pdf + 离线 OCR（Sdcb.PaddleOCR + Docnet）   |
| `PromptCraft.Service/AssetGen/AssetImageGenerationService.cs`    | 资产生图：T2I/I2I 环境自适应模板、/object\_info 探测、pending 人工验收       |
| `PromptCraft.Service/ReverseCaptionService.cs`                   | 反向标注 / 批量打标（多模型提示词工程）                                    |
| `PromptCraft.Service/ProviderService.cs`                         | 模型提供方管理（Ollama / OpenAI 兼容等，默认模型回落）                      |
| `PromptCraft.Service/OllamaApiHelper.cs` / `OpenAiHttpHelper.cs` | LLM API 客户端（Ollama / OpenAI 兼容协议）                        |
| `PromptCraft.Service/LogService.cs`                              | 日志实现：Channel 队列、每日轮转、errors.log、全局异常                     |
| `PromptCraft.Service/Diagnostics/CrashGuard.cs`                  | 崩溃防护：托管异常 + Windows 原生崩溃 → minidump                      |
| `PromptCraft.Data/ComfyDbContext.cs`                             | ComfyUI 主库（13 张表，EF Fluent 配置）                           |
| `PromptCraft.Data/ComfyDbMigrator.cs`                            | 手工 schema 升级（PRAGMA 补列 / 建表，幂等）                          |
| `PromptCraft.Data/ComfyRepositories.cs`                          | 9 个 Repository（IDbContextFactory + 每操作独立上下文）             |
| `PromptCraft.Models/ComfyUI/ComfyApiModels.cs`                   | ComfyUI API DTO（/object\_info、/api/prompt 等）             |
| `PromptCraft.UILib/Markdown/MarkdownRenderer.cs`                 | Markdown 渲染器（增量更新、高亮、异步图片）                               |
| `PromptCraft.Dock/Index.axaml`                                   | 停靠布局样式入口（DockFluentTheme + 本地主题）                         |



***

## 数据存储



| 数据                 | 路径                                                                       | 说明                                                                 |
| ------------------ | ------------------------------------------------------------------------ | ------------------------------------------------------------------ |
| settings.db        | `%APPDATA%/PromptCraft/settings.db`                                      | 应用配置（单行表 AppConfig，Id=1）                                           |
| comfyui.db         | `%APPDATA%/PromptCraft/comfy_data/comfyui.db`（ComfySettings.DataDir 可配置） | ComfyUI 业务库（13 张表）                                                 |
| logs/              | `%APPDATA%/PromptCraft/logs/`                                            | log-yyyyMMdd.log（每日轮转，保留 7 天）+ errors.log                          |
| thumbnails/        | `<DataDir>/thumbnails/`                                                  | 缩略图（相对路径 SHA256 前 16 位 + 尺寸后缀 .jpg）                                |
| assetgen\_pending/ | `%APPDATA%/PromptCraft/comfy_data/assetgen_pending/`                     | 资产生图待人工验收目录                                                        |
| crashes/           | `%APPDATA%/PromptCraft/crashes/`                                         | 崩溃 minidump（保留 20 份；环境变量 `PROMPTCRAFT_CRASH_DUMP_MINI=1` 切轻量 dump） |

**ComfyDbContext 表（13 张）**：ImageMetadata（ImageInfo）、ImageStatuses、Tags、ImageTags、WorkflowTags、BlacklistedHashes、Workflows、WorkflowInputs、WorkflowFolders、WorkflowJobs、JobOutputs、WorkflowParams、ImagePrompts。

> Schema 升级策略：
>
> `EnsureCreated`
>
> （无 EF 迁移）+ 
>
> `ComfyDbMigrator`
>
>  手工 PRAGMA 补列 / 建表（幂等）。



***

## 构建与运行

### 环境要求



* **.NET 10 SDK**（10.0.100+）

* 可选：Android /iOS workload（构建对应平台头时）

### 构建



```
# 构建主应用（Windows 桌面，推荐）
dotnet build PromptCraft\PromptCraft.csproj

# 构建桌面启动工程
dotnet build PromptCraft.Desktop\PromptCraft.Desktop.csproj

# 一键聚合构建（全部项目）
dotnet build build_all.csproj

# 运行测试
dotnet test PromptCraft.Test\PromptCraft.Test.csproj
```

### 运行

启动项目为 **PromptCraft.Desktop**（WinExe）。运行依赖：



* **ComfyUI**（默认 `http://127.0.0.1:8188`）：图库同步、工作流执行、资产生图需要；未启动时应用仍可运行（WS 重试 3 次后弹 Toast）

* **Ollama / OpenAI 兼容 API**：小说转提示词、反向标注等 LLM 功能需要（设置页配置 Provider）

* 知识库 OCR / PDF 解析：内置模型，完全离线，无需联网



***

## 测试

`PromptCraft.Test`（MSTest，net10.0）：**108 个测试，102 通过，6 个既有失败**（与升级前一致，无回归）：



* `OllamaApiHelperTests` × 3（Ollama 客户端行为失配）

* `OpenAiHttpHelperTests.Chat_404_ThrowsModelNotFound`

* `ProviderServiceTests` × 2（"不自动提升默认模型" 实现与测试预期不一致）

覆盖范围：资产生图模板（T2I/I2I、环境探测）、知识库 OCR/PDF 解析、小说转提示词流水线（镜头规划、Minimax 格式、Seedance 回退、资产扫描）、反向标注后处理、扩写 / 模仿、百度翻译签名、视频抽帧、Provider 持久化与默认值等。



***

## 开发约定



* **命名**：接口一律 `IBase*` / `IXxxService`；实现放 `PromptCraft.Service`；类名 PascalCase，字段 `_camelCase`；事件名收 `EventNameConst`，配置名收 `ConfigNameConst`

* **MVVM**：`[ObservableProperty]` + `[RelayCommand]` 源生成器；XAML 编译绑定（x:DataType）

* **日志**：禁止空 catch；预期内可恢复失败记 Warn，业务 / 数据失败记 Error 并附异常，诊断记 Debug，成功关键路径记 Info；DI 前用 `LogService.Instance` 静态引导

* **时间**：统一 UTC（DateTime.UtcNow）

* **仓储**：每操作独立 DbContext（防追踪脏读）；删除用逻辑删除（IsDeleted）

* **本地化**：UI 文案全部走 i18n JSON（zh-CN /en-US），XAML `{i18n:Localize}`，VM 注入 ILocalizer

* **注释**：ComfyUI / 推理模块有详尽 XML 注释（中文），关键业务写明 "为什么这样做"

详细规范与踩坑记录见 `LOGGING.md` 与 `PROJECT_ANALYSIS.md`。



***

## 路线图

迭代计划见 `docs/v2-迭代待办.md`。近期方向：



* [x] 知识库深度解析（离线 OCR + PDF）接入小说转提示词

* [x] 资产生图环境自适应模板（T2I/I2I、模型探测、人工验收）

* [x] 项目改名 PromptCraft、升级 .NET 10、全新应用图标

* [ ] ComfyUI 模型探测解析修复后开放资产生图 UI 入口

* [ ] 缺口提示词漏产自动重试

* [ ] README.en.md 同步更新



***

## 参与贡献



1. Fork 本仓库

2. 新建功能分支（`Feat_xxx`）

3. 提交代码（遵循 .editorconfig 与开发约定）

4. 新建 Pull Request



***

## 许可证

[MIT](LICENSE)