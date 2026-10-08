# PromptCraft

> AI Prompt Crafting Studio — a cross-platform desktop application built around ComfyUI, integrating prompt engineering, novel-to-prompt pipelines, asset image generation, gallery management, training datasets and fully offline capabilities.

**PromptCraft** is a cross-platform desktop application built on **Avalonia 12** (Windows / Linux / macOS, with WASM / Android / iOS entry points). It chains "prompt crafting" with "ComfyUI image generation" into one end-to-end pipeline: from novels, knowledge bases and reference images, to prompt engineering and batch captioning, then to workflow execution and asset management — all in a single interface.

- **Baseline**: .NET 10 (net10.0) / C# latest / Avalonia 12.1 / SukiUI 7 / EFCore 9 + SQLite
- **Solution**: `PromptCraft.sln` (15 projects + 1 aggregate build entry)
- **License**: MIT

---

## Table of Contents

- [Features](#features)
- [Tech Stack](#tech-stack)
- [Architecture](#architecture)
- [Directory Structure](#directory-structure)
- [Projects](#projects)
- [Key Files](#key-files)
- [Data Storage](#data-storage)
- [Build & Run](#build--run)
- [Testing](#testing)
- [Development Conventions](#development-conventions)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

---

## Features

### 1. Novel → Prompt Pipeline (NovelToPrompt)

Automatically turns novel chapters into executable shot-by-shot prompts through a seven-stage pipeline orchestration (`NovelToPromptService`):

| Stage | Content |
|---|---|
| S1–S4 | Single-text stages via LLM: summary, characters, scenes, details (knowledge-base directory text injected as reference when enabled) |
| S5 | Asset scan: LLM extracts an asset requirement list → scans the asset directory → reports missing assets; gap prompts output in batches (with truncation continuation / missing-fill) |
| S6 | Shot-planning gate: per-scene shot JSON generation; long scenes are automatically batched (≤5 shots/batch) and assembled to avoid truncation |
| S7 | Per-shot prompts: H3 Chinese direct / MiniMax six-format (full_reference) / continuous-story director's desk / Seedance 2.0; per-prompt truncation auto-continuation, merged by scene into sub-batches |

- Output language: Chinese / English selectable; every stage forced to the selected language
- Model: page-selected model preferred, falls back to expand default; temperature 0.7 / top_p 0.9; output cleaned via StripThinkingTags
- UI: `NovelToPromptView` (worldbuilding / TAG handling / shot planning / prompt generation / asset gaps)

### 2. Knowledge Base Deep Parsing (Offline OCR + PDF)

`KnowledgeDocumentReader` lets the knowledge base digest more than plain text:

- **Formats**: txt / docx / pdf / images
- **PDF**: Docnet.Core (PDFium) renders pages to bitmaps so scanned PDFs can be read too
- **OCR**: Sdcb.SimdPaddleOCR (PP-OCRv6 Chinese model, embedded model, pure managed SIMD inference, Apache-2.0, **fully offline, no cloud service**)
- 80k character cap per document, dual cache keyed by (path, mtime)

### 3. Asset Image Generation (Environment-Adaptive Templates)

`AssetImageGenerationService` targets "gap prompts → batch asset images":

- **Text-to-image / image-to-image dual skeletons**, not bound to a specific model (adapts to any locally installed checkpoint / LoRA)
- **Environment-adaptive**: probes `/object_info` before submission + dry-run fail-fast
- **Human acceptance**: generated assets land in a `pending` directory for manual confirmation before being filed, avoiding dirty data
- 5-minute polling, SD1.5-class 768-series default output params, default model key `asset_gen.default_ckpt`

> Note: the asset-generation template service is built-in and test-covered; the UI trigger button is currently hidden (pending the ComfyUI model-detection parsing fix before reopening).

### 4. Reverse Captioning / Batch Tagging

Multi-model reverse prompts and description generation:

- **Multi-model prompt engineering**: Danbooru, JoyCaption, Natural Language, Descriptive, AltProse, Anima3, ToriiGate, MiniMax and more
- Batch processing, tag-line sanitization, output length/capability constraints, thinking-tag heuristics
- Bilingual output, Baidu Translate inter-conversion (`TranslateService`)

### 5. ComfyUI Gallery

- **Image sync**: scans ComfyUI output directory (png/jpg/jpeg/webp/bmp, recursive) → parses PNG tEXt/iTXt/zTXt and WebP EXIF metadata → skips images without ComfyUI metadata / workflow ID
- **Metadata extraction**: positive/negative prompts, model, seed, steps, CFG, sampler, scheduler, width/height, LoRAs, full node snapshot; workflows stored as "structural" versions (params stripped + GZip), rehydrated to full workflows on detail view
- **Deduplication**: workflows unique by ComfyUI top-level UUID; prompts unique by (WorkflowId, PromptHash); legacy backfill + thumbnail-name migration
- **Gallery UI**: streaming pagination of thumbnails, search (filename/tag/hash), filters (all/favorites/NSFW/multi-tag), batch delete / batch tag, tag-drawer management
- **Image detail**: large preview, favorite / NSFW / rating / notes, tags, extracted prompts & params, delete
- **Image comparison**: draggable splitter; three-layer diff — parameter diff, positive/negative prompt character-level diff (TextDiff), workflow node-level diff (WorkflowDiffer)

### 6. ComfyUI Workflow Management

- **Sync**: pulls user workflow files from ComfyUI (`/api/userdata`), upserts by SourcePath
- **Editing**: AvaloniaEdit + TextMate syntax highlighting (auto dark/light theme), thumbnail picker for preview
- **Execution**: UI→API format auto-conversion (consistent with ComfyUI frontend convertWorkflowToAPI: /object_info field-order mapping, comment-node filtering, widgets whitelist) → POST /api/prompt (fixed client_id + extra_pnginfo so PNGs carry the workflow) → background tracking
- **Execution tracking**: WebSocket real-time messages (start/executing/progress/executed/success/error/interrupted) + 2s /history polling fallback (polls to terminal state when WS is down) → terminal state persisted → **output images auto-added to the gallery**
- **Parameter config**: per-node/field fixed values or random ranges (seed and other numeric fields support random), stored as one WorkflowParams per workflow
- **Execution history**: status/progress/node logs/output images; open detail/compare, delete single / clear all

### 7. Prompt Library

- Local prompt library: card browsing, search, multi-tag filtering
- Batch tagging (PromptBatchTag), tag-management drawer, edit dialog
- Big-image preview, cover drag-sort, in-library import/organization

### 8. Prompt Engineering & PE Editor

- **PromptEngineering**: engineered prompt construction
- **PE Editor** (PeEditor / PeImport): edit / import workbench

### 9. Training Datasets

- **TrainDatasets**: dataset list + detail (TrainDatasetDetail), batch-adding images from paths (`DatasetService.AddImagesFromPaths`)
- Auxiliary management aimed at ComfyUI / SD training-data organization

### 10. Expand / Imitation / Prompt Plaza

- **Expand**: rules-based expansion (ExpandRules) + MiniMax scene assembly (six-format / continuous story / director's desk), reusing the same MiniMaxAssembler
- **Imitation**: imitation prompts generated from reference examples
- **Prompt Plaza**: cloud prompt resource browsing / fetching (CloudPromptPlazaSource)

### 11. App Shell & Settings

- SukiUI main window: side menu navigation, tray icon, Toast / dialog host
- **Settings page**: light/dark theme, background styles (Flat/Gradient/GradientSoft/GradientDarker/Bubble), background animation/transition toggles, theme color (built-in palette + custom), language zh-CN/en-US instant switching, ComfyUI connection config (address/output dir/data dir/connection test), log level
- Config persistence: `%APPDATA%/PromptCraft/settings.db` (SQLite single-row table), legacy JSON config auto-migrated

### 12. Logging

- 6 levels (Trace/Debug/Info/Warn/Error/Fatal) + category + exception formatting
- Daily rotation `logs/log-yyyyMMdd.log` (keep 7 days), Error/Fatal accumulated in `errors.log`
- Non-blocking writes (Channel queue + in-memory buffer + LogWritten event), global exception capture (AppDomain + TaskScheduler)
- Log page: real-time reverse list, level/keyword filtering, red error highlighting, error export, open directory
- Native Windows crash → in-process SEH filter MiniDumpWriteDump; hard crash → WER LocalDumps fallback (`CrashGuard`)

### 13. Media Capabilities

- Inline image/video preview (LibVLCSharp software rendering, in-material-card playback aligned with PromptMaster `<video>/<audio>`)
- Video frame extraction (VideoFrameGrabber / VideoFrameExtractor)
- Generic controls: clipboard, folder pickers, multi-select dropdowns, waterfall panel, etc.

---

## Tech Stack

| Category | Technology / Package | Version |
|---|---|---|
| Runtime | .NET / C# | **net10.0**, LangVersion latest |
| UI framework | Avalonia (Skia / Fluent / Inter / Android / iOS / Browser / ColorPicker / DataGrid) | 12.1.2 |
| UI theme | SukiUI (window / side menu / Toast / dialog / MessageBox) | 7.0.2-nightly20261002.475 |
| MVVM | CommunityToolkit.Mvvm (source generators) | 8.4.0 |
| DI | Microsoft.Extensions.DependencyInjection | 10.0.12 |
| ORM | Microsoft.EntityFrameworkCore.Sqlite | 9.0.4 |
| Docking | Dock.Avalonia / Dock.Model.Avalonia (custom theme in PromptCraft.Dock) | 12.1.0.6 |
| Code editor | Avalonia.AvaloniaEdit + AvaloniaEdit.TextMate | 12.0.0 |
| Markdown | AvaloniaHotMarkdown + built-in renderer in PromptCraft.UILib | 0.2.5 |
| Icons | Material.Icons.Avalonia | 3.0.2 |
| Imaging | SkiaSharp | 3.119.4 |
| Charts | LiveChartsCore.SkiaSharpView.Avalonia | 2.0.0-rc5.4 |
| Media | LibVLCSharp / LibVLCSharp.Avalonia / VideoLAN.LibVLC.Windows | 3.10.1 / 3.0.24 |
| **Offline OCR** | **Sdcb.SimdPaddleOCR + ChineseV6Tiny** (PP-OCRv6, Apache-2.0) | 1.4.2 / 1.0.0 |
| **PDF rendering** | **Docnet.Core** (PDFium, MIT) | 2.6.0 |
| Localization | Ke.Bee.Localization (JSON provider, vendored in-repo) | 0.1.1 |
| JSON | System.Text.Json | 10.0.2 |
| Obfuscation | Obfuscar.MsBuild | 2.2.50 |
| SVG | Svg.Controls.Avalonia | 12.0.0.17 |
| Testing | MSTest | — |
| Package management | Central Package Management (Directory.Packages.props) | — |

---

## Architecture

Clean layering: **Shells (platform entry) → Main app (UI + business) → Service layer → Data layer → Interface layer → Foundation libraries**.

```
PromptCraft.Desktop / .Browser / .Android / .iOS    platform entries (net10.0 / -browser / -android / -ios)
        └── PromptCraft (main app: ViewModels / Views / Controls / Styles / Assets)
                ├── PromptCraft.Service   service implementations (ComfyUI, inference pipelines, OCR, asset gen, logging…)
                ├── PromptCraft.Data      EFCore + SQLite (settings.db / comfyui.db)
                ├── PromptCraft.Models    domain models (ComfyUI, Inference, config, prompt library…)
                ├── PromptCraft.Interfaces  interface layer (services / repositories / events / logging / views / pages…)
                ├── PromptCraft.Consts    constants (config names / event names)
                ├── BaseClassLib          foundation library (MVVM bases, theme, language, DI extensions)
                ├── PromptCraft.UILib     Markdown rendering library
                ├── PromptCraft.Dock      docking-layout SukiUI theme customization
                └── Ke.Bee.Localization   JSON localization library
```

**Core architecture conventions**:

- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]` / `[RelayCommand]`); `ViewModelBase : ModelBase(ObservableValidator)`; XAML uses `x:DataType` + compiled bindings (`AvaloniaUseCompiledBindingsByDefault=true`)
- **View location**: `ViewService` maintains the VM→View mapping table; `ViewLocator` (IDataTemplate) creates views by VM instance and **caches per instance**
- **DI**: Microsoft.Extensions.DependencyInjection with three registration entries (platform-agnostic base services / views & VMs / ComfyUI family: Client, WS Hub, Service, 9 repositories, 2 DbContexts)
- **Interface + implementation + DI**: all base services go through `IBaseXxx` interfaces → implementations in `PromptCraft.Service` → DI registration
- **Event bus**: `NoticeService` (IBaseNotice) — string event names (EventNameConst) publish/subscribe, thread-safe, handler exceptions auto-logged as Error
- **Repository pattern**: repositories inject `IDbContextFactory<ComfyDbContext>`, each operation opens its own DbContext (avoids tracked dirty reads), read-only queries use `AsNoTracking`
- **Static bootstrap points**: static instances used before DI is ready (LogService.Instance / ComfyUIWebSocketHub.Instance / AppInitData.Config)
- **Localization**: Ke.Bee.Localization (zh-CN / en-US JSON), XAML `{i18n:Localize}`, VMs inject ILocalizer; language switch broadcast via event bus
- **Theming**: SukiTheme singleton; light/dark, background styles/animation/transitions, theme color applied from config at startup and written back on exit

**Startup flow (Desktop)**: `Program.Main` (log bootstrap + cleanup) → `App.Initialize` (global exception handlers → ensure config DB → load config → apply log level → migrate legacy JSON → load XAML) → `OnFrameworkInitializationCompleted` (build DI → upgrade Comfy DB schema → register view locator → create main window → start ComfyUI WebSocket long connection).

---

## Directory Structure

```
promptcraft/
├── PromptCraft.sln                     # solution (15 projects)
├── Directory.Packages.props            # Central Package Management (CPM)
├── build_all.csproj                    # one-click aggregate build entry
├── README.md / README.en.md            # docs (this file) / English
├── PROJECT_ANALYSIS.md                 # AI-generated project analysis report
├── LOGGING.md                          # logging system design doc (with pitfalls)
├── AGENTS.md / .editorconfig / .gitattributes / .gitignore / LICENSE
├── .mcp.json / create_mcp.py           # MCP config & generator script
├── docs/
│   └── v2-迭代待办.md                    # v2 iteration backlog
│
├── BaseClassLib/                       # foundation library (no business logic)
│   ├── ModelBase.cs                    # ObservableValidator base (DisplayName/Icon/Index/SideMenu)
│   ├── Extends/                        # LanguageExtend, ServiceProviderExtend, SukiMessageBox…FactoryExtend
│   ├── Manager/ViewManger.cs
│   └── Models/                         # Config, AddThemeModel, LanguageEnum, ListItem<T>
│
├── PromptCraft.Consts/                 # constants layer (zero dependencies)
│   ├── Config/ConfigNameConst.cs       # config-name constants
│   └── Event/EventNameConst.cs         # event-name constants
│
├── PromptCraft.Interfaces/             # interface layer
│   ├── IBaseNotice.cs / IBaseLogService.cs / IBaseViewService.cs / IBasePageService.cs
│   ├── IProviderService.cs / IComfyUIService.cs / IComfyUIClient.cs / IComfyUIWebSocketHub.cs
│   ├── INovelToPromptService.cs / IReverseCaptionService.cs / IExpandService.cs / IImitationService.cs
│   ├── IPromptLibraryService.cs / IPromptPlazaSource.cs / ITranslateService.cs / IWorkspaceService.cs
│   ├── repository interfaces: IAppSettingsRepository / IImageMetadataRepository / IWorkflowRepository / ITagRepository…
│   └── LogLevel.cs / LogEntry.cs
│
├── PromptCraft.Models/                 # domain models (POCO / entities)
│   ├── AppConfig.cs / AppSetting.cs / ProviderConfig.cs / PlazaPrompt.cs / PromptLibraryEntities.cs
│   ├── ComfyUI/                        # ComfyApiModels (/object_info etc. API DTOs), ComfyModels, TagPalette
│   ├── Inference/                      # ChatCompletionModels, ReverseCaptionModels, ExpandModels, ImitationModels
│   │   └── Novel/NovelModels.cs
│   └── LogLevel.cs
│
├── PromptCraft.Data/                   # data layer (EFCore + SQLite)
│   ├── SettingsDbContext.cs            # config DB (%APPDATA%/PromptCraft/settings.db)
│   ├── ComfyDbContext.cs               # ComfyUI business DB (13 tables, Fluent config)
│   ├── ComfyDbMigrator.cs              # manual schema upgrades (PRAGMA add-column/create-table, idempotent)
│   ├── ConfigRepository.cs             # config repository (static/instance, JSON↔DB migration)
│   ├── ComfyRepositories.cs            # 9 repositories (IDbContextFactory + per-op context)
│   └── ComfyImageMetadata.cs / ComfyVideoMetadata.cs
│
├── PromptCraft.Service/                # service implementations (core business)
│   ├── ConfigService.cs                # DI registration extension + base services
│   ├── ProviderService.cs              # model providers (Ollama / OpenAI-compatible…)
│   ├── OllamaApiHelper.cs / OpenAiHttpHelper.cs   # LLM API clients
│   ├── ComfyUIService.cs / ComfyUIClient / ComfyUIWebSocketHub.cs   # ComfyUI connection/execution
│   ├── ComfyWorkflowNormalizer.cs / WorkflowDiffer.cs / TextDiff.cs
│   ├── KnowledgeDocumentReader.cs      # knowledge-base deep parsing (txt/docx/pdf + offline OCR)
│   ├── AssetGen/AssetImageGenerationService.cs   # asset image generation (T2I/I2I adaptive templates)
│   ├── Inference/
│   │   ├── Novel/                      # NovelToPromptService, H3PromptBuilder, SeedancePromptBlocks…
│   │   ├── Reverse/                    # Danbooru/JoyCaption/Anima3/NaturalLanguage/Descriptive/AltProse/
│   │   │                               #   ToriiGate/MiniMax prompt engineering
│   │   └── Minimax/                    # MiniMaxAssembler, MediaVision, ExpandMedia
│   ├── PromptPlaza/                    # prompt plaza (cloud resources)
│   ├── DatasetService.cs / FolderService.cs / PromptLibraryService.cs / WorkspaceService.cs
│   ├── ExpandService.cs / ImitationService.cs / ReverseCaptionService.cs
│   ├── TranslateService.cs             # Baidu Translate
│   ├── LogService.cs / Diagnostics/CrashGuard.cs
│   └── ClipboardService.cs / NoticeService.cs / ViewService.cs / PageService.cs
│
├── PromptCraft.UILib/                  # Markdown rendering library
│   └── Markdown/                       # MarkdownRenderer(+Input/Utils), MarkdownTextBlock, CodeBlock,
│                                       #   SyntaxHighlighting, AsyncImageLoader, Styles.axaml…
│
├── PromptCraft.Dock/                   # Dock.Avalonia SukiUI theme customization
│   └── Converters/                     # 8 docking-related converters
│
├── PromptCraft/                        # ★ main app (UI + business)
│   ├── App.axaml(.cs)                  # startup bootstrap, theme/language, tray icon
│   ├── BaseModel/                      # ViewModelBase, PageBase
│   ├── Common/                         # StorageService, ViewLocator
│   ├── Controls/                       # CodeEditor, MediaVideoView, MultiSelectDropDown, TagChipTextBox,
│   │                                   #   PromptLibraryTagDrawer, WaterfallPanel…
│   ├── Converters/                     # BoolToEyeIconConverter etc.
│   ├── DialogView/                     # CustomThemColor (custom theme color)
│   ├── Service/                        # LibVlcProvider
│   ├── StaticData/AppInitData.cs       # global config static entry
│   ├── Utils/                          # ViewUtils (DI registration), FolderClipboardService, VideoFrameGrabber…
│   ├── ViewModels/
│   │   ├── AppViewModel.cs / MainViewModel.cs / CustomThemColorModel.cs
│   │   ├── ComfyUI/                    # ComfyGalleryModel, ComfyWorkflowModel, ImageDetailModel,
│   │   │                               #   WorkflowDetailModel, ImageCompareModel, ThumbnailPickerModel,
│   │   │                               #   WorkflowParamsModel, TagManagerModel, BatchTagModel…
│   │   ├── PromptLibrary/              # PromptLibraryModel, PromptEditModel, PromptBatchTagModel…
│   │   ├── System/                     # NovelToPromptViewModel, PromptEngineeringViewModel, PeEditorModel,
│   │   │                               #   ReverseCaptionViewModel, TrainDatasetsViewModel,
│   │   │                               #   TrainDatasetDetailViewModel, ComfyUIViewModel, ExpandViewModel,
│   │   │                               #   ImitationViewModel, PromptPlazaViewModel, SettingModel, LogViewModel,
│   │   │                               #   FolderManageViewModel…
│   │   └── Test/TestViewModel.cs
│   ├── Views/
│   │   ├── MainWindow.axaml            # SukiWindow + side-menu navigation host
│   │   ├── ComfyUI/                    # ComfyGallery, ComfyWorkflowView, ImageDetailView, WorkflowDetailView,
│   │   │                               #   ImageCompareView, ThumbnailPickerView, WorkflowParamsView,
│   │   │                               #   TagManagerView, BatchTagView…
│   │   ├── PromptLibrary/              # PromptLibraryView, PromptEditDialogView, PromptBigImageView…
│   │   ├── System/                     # NovelToPromptView, PromptEngineeringView, PeEditorView, PeImportView,
│   │   │                               #   ReverseCaptionView, TrainDatasetsView, TrainDatasetDetailView,
│   │   │                               #   ComfyUIView, ExpandView, ImitationView, PromptPlazaView,
│   │   │                               #   Setting.axaml, LogView.axaml, FolderManageView…
│   │   └── Test/
│   ├── Styles/                         # AppColors, CommonStyles, GlassCardStyles, TextStyles…
│   └── Assets/                         # icons (logo.ico 7 sizes / node.png), i18n/zh-CN.json, en-US.json,
│                                       #   prompts/, workflow_placeholder.png
│
├── PromptCraft.Desktop/                # Windows desktop entry (Program.cs, app.manifest, publish config)
├── PromptCraft.Browser/                # WASM entry (net10.0-browser)
├── PromptCraft.Android/                # Android entry (net10.0-android)
├── PromptCraft.iOS/                    # iOS entry (net10.0-ios)
├── PromptCraft.Test/                   # MSTest test project (19 test files)
└── Ke.Bee.Localization/                # JSON localization library (Avalonia, vendored)
```

---

## Projects

| Project | Type | Responsibility |
|---|---|---|
| PromptCraft | App (net10.0) | Main app: all UI (ViewModels / Views / Controls / Styles) and page-level business |
| PromptCraft.Desktop | Executable (net10.0) | Windows desktop entry: log bootstrap, top-level Fatal handling, WinExe publish |
| PromptCraft.Browser | Executable (net10.0-browser) | WASM entry (Avalonia.Browser) |
| PromptCraft.Android | Executable (net10.0-android) | Android entry (Avalonia.Android) |
| PromptCraft.iOS | Executable (net10.0-ios) | iOS entry (Avalonia.iOS) |
| PromptCraft.Service | Library | Service implementations: ComfyUI connection & execution, inference pipelines, OCR, asset generation, logging, prompt library, datasets, etc. |
| PromptCraft.Data | Library | Data layer: EFCore + SQLite (settings.db / comfyui.db), 9 repositories, manual schema migration |
| PromptCraft.Models | Library | Domain models: ComfyUI API DTOs, inference models, config, prompt-library entities |
| PromptCraft.Interfaces | Library | All service/repository/event/log/view/page interfaces (IBaseXxx convention) |
| PromptCraft.Consts | Library | Config-name / event-name constants (zero dependencies) |
| BaseClassLib | Library | Foundation: MVVM bases, theme/language models, DI extensions, MessageBox factory |
| PromptCraft.UILib | Library | Markdown rendering (Markdig + TextMate/ColorCode highlighting + async images + SVG) |
| PromptCraft.Dock | Library | Dock.Avalonia SukiUI theme customization (docking control templates & converters) |
| Ke.Bee.Localization | Library | Avalonia JSON localization library (vendored copy) |
| PromptCraft.Test | Test (net10.0) | MSTest: 108 tests |
| build_all.csproj | Aggregate (net10.0) | One-click aggregate build entry (compiles no code itself) |

---

## Key Files

| File | Description |
|---|---|
| `PromptCraft.Desktop/Program.cs` | Desktop entry: log bootstrap/cleanup, top-level Fatal, Avalonia startup |
| `PromptCraft/App.axaml(.cs)` | App startup: exception handlers → ensure DB → load config → DI → view location → WS long connection; tray/theme/language |
| `PromptCraft/Common/StorageService.cs` | File pickers, AppData paths, JSON config I/O, filename sanitization |
| `PromptCraft/Common/ViewLocator.cs` | VM→View mapping + view caching (IDataTemplate) |
| `PromptCraft/Utils/ViewUtils.cs` | DI registration: 12 views+VMs + ComfyUI service family |
| `PromptCraft/StaticData/AppInitData.cs` | Global config static entry (Config + load/save) |
| `PromptCraft.Service/ConfigService.cs` | DI registration extension: InitPromptCraftServices (base service family) |
| `PromptCraft.Service/ComfyUIService.cs` | Workflow sync / execution tracking / deletion + auto-ingest of outputs |
| `PromptCraft.Service/ComfyUIWebSocketHub.cs` | App-level WS long connection (3 retries + auto-reconnect) |
| `PromptCraft.Service/ComfyWorkflowNormalizer.cs` | Workflow structure/params separation & reassembly (dedup core) |
| `PromptCraft.Service/ComfyImageMetadata.cs` | PNG/WebP metadata reading, param extraction, GZip encode/decode |
| `PromptCraft.Service/NovelToPromptService.cs` | Seven-stage novel→prompt pipeline orchestration (S1–S7) |
| `PromptCraft.Service/KnowledgeDocumentReader.cs` | Knowledge-base deep parsing: txt/docx/pdf + offline OCR (Sdcb.PaddleOCR + Docnet) |
| `PromptCraft.Service/AssetGen/AssetImageGenerationService.cs` | Asset generation: T2I/I2I adaptive templates, /object_info probing, pending acceptance |
| `PromptCraft.Service/ReverseCaptionService.cs` | Reverse captioning / batch tagging (multi-model prompt engineering) |
| `PromptCraft.Service/ProviderService.cs` | Model provider management (Ollama / OpenAI-compatible etc., default-model fallback) |
| `PromptCraft.Service/OllamaApiHelper.cs` / `OpenAiHttpHelper.cs` | LLM API clients (Ollama / OpenAI-compatible protocol) |
| `PromptCraft.Service/LogService.cs` | Logging: Channel queue, daily rotation, errors.log, global exceptions |
| `PromptCraft.Service/Diagnostics/CrashGuard.cs` | Crash protection: managed exceptions + native Windows crashes → minidump |
| `PromptCraft.Data/ComfyDbContext.cs` | ComfyUI main DB (13 tables, EF Fluent config) |
| `PromptCraft.Data/ComfyDbMigrator.cs` | Manual schema upgrade (PRAGMA add-column/create-table, idempotent) |
| `PromptCraft.Data/ComfyRepositories.cs` | 9 repositories (IDbContextFactory + per-op context) |
| `PromptCraft.Models/ComfyUI/ComfyApiModels.cs` | ComfyUI API DTOs (/object_info, /api/prompt, etc.) |
| `PromptCraft.UILib/Markdown/MarkdownRenderer.cs` | Markdown renderer (incremental updates, highlighting, async images) |
| `PromptCraft.Dock/Index.axaml` | Docking-layout styles entry (DockFluentTheme + local theme) |

---

## Data Storage

| Data | Path | Description |
|---|---|---|
| settings.db | `%APPDATA%/PromptCraft/settings.db` | App config (single-row table AppConfig, Id=1) |
| comfyui.db | `%APPDATA%/PromptCraft/comfy_data/comfyui.db` (configurable via ComfySettings.DataDir) | ComfyUI business DB (13 tables) |
| logs/ | `%APPDATA%/PromptCraft/logs/` | log-yyyyMMdd.log (daily rotation, 7-day retention) + errors.log |
| thumbnails/ | `<DataDir>/thumbnails/` | Thumbnails (relative-path SHA256 first 16 chars + size suffix .jpg) |
| assetgen_pending/ | `%APPDATA%/PromptCraft/comfy_data/assetgen_pending/` | Asset-generation pending human-acceptance directory |
| crashes/ | `%APPDATA%/PromptCraft/crashes/` | Crash minidumps (keep 20; env `PROMPTCRAFT_CRASH_DUMP_MINI=1` for lightweight dumps) |

**ComfyDbContext tables (13)**: ImageMetadata (ImageInfo), ImageStatuses, Tags, ImageTags, WorkflowTags, BlacklistedHashes, Workflows, WorkflowInputs, WorkflowFolders, WorkflowJobs, JobOutputs, WorkflowParams, ImagePrompts.

> Schema-upgrade strategy: `EnsureCreated` (no EF migrations) + `ComfyDbMigrator` manual PRAGMA add-column/create-table (idempotent).

---

## Build & Run

### Prerequisites

- **.NET 10 SDK** (10.0.100+)
- Optional: Android / iOS workload (when building those platform heads)

### Build

```bash
# Build the main app (Windows desktop, recommended)
dotnet build PromptCraft\PromptCraft.csproj

# Build the desktop launcher project
dotnet build PromptCraft.Desktop\PromptCraft.Desktop.csproj

# One-click aggregate build (all projects)
dotnet build build_all.csproj

# Run tests
dotnet test PromptCraft.Test\PromptCraft.Test.csproj
```

### Run

Launch project: **PromptCraft.Desktop** (WinExe). Runtime dependencies:

- **ComfyUI** (default `http://127.0.0.1:8188`): gallery sync, workflow execution, asset generation need it; the app still runs when it is down (WS retries 3 times then shows a Toast)
- **Ollama / OpenAI-compatible API**: LLM features (novel-to-prompt, reverse captioning, etc.) need a provider configured in Settings
- Knowledge-base OCR / PDF parsing: built-in models, fully offline, no network needed

---

## Testing

`PromptCraft.Test` (MSTest, net10.0): **108 tests, 102 passing, 6 pre-existing failures** (identical to before the upgrade, no regressions):

- `OllamaApiHelperTests` × 3 (Ollama client behavior mismatch)
- `OpenAiHttpHelperTests.Chat_404_ThrowsModelNotFound`
- `ProviderServiceTests` × 2 ("does not auto-promote default model" implementation vs. test expectation)

Coverage: asset-generation templates (T2I/I2I, environment probing), knowledge-base OCR/PDF parsing, novel-to-prompt pipeline (shot planning, Minimax formats, Seedance fallback, asset scanning), reverse-caption post-processing, expand/imitation, Baidu translate signing, video frame extraction, provider persistence & defaults, etc.

---

## Development Conventions

- **Naming**: interfaces use `IBase*` / `IXxxService`; implementations live in `PromptCraft.Service`; class names PascalCase, fields `_camelCase`; event names in `EventNameConst`, config names in `ConfigNameConst`
- **MVVM**: `[ObservableProperty]` + `[RelayCommand]` source generators; XAML compiled bindings (x:DataType)
- **Logging**: no empty catch blocks; recoverable expected failures → Warn; business/data failures → Error with exception; diagnostics → Debug; successful critical paths → Info; use `LogService.Instance` for pre-DI bootstrapping
- **Time**: unified UTC (DateTime.UtcNow)
- **Repositories**: per-operation DbContext (avoids tracked dirty reads); logical deletion via IsDeleted
- **Localization**: all UI copy goes through i18n JSON (zh-CN / en-US), XAML `{i18n:Localize}`, VMs inject ILocalizer
- **Comments**: ComfyUI / inference modules carry detailed XML docs (Chinese), with key business decisions explaining "why"

Full conventions and pitfall records: `LOGGING.md` and `PROJECT_ANALYSIS.md`.

---

## Roadmap

Iteration backlog: `docs/v2-迭代待办.md`. Recent directions:

- [x] Knowledge-base deep parsing (offline OCR + PDF) wired into novel-to-prompt
- [x] Asset-generation environment-adaptive templates (T2I/I2I, model probing, human acceptance)
- [x] Renamed to PromptCraft, upgraded to .NET 10, brand-new app icon
- [ ] Reopen asset-generation UI entry after the ComfyUI model-detection parsing fix
- [ ] Auto-retry for gap prompts on under-production
- [ ] Keep README.en.md in sync

---

## Contributing

1. Fork this repository
2. Create a feature branch (`Feat_xxx`)
3. Commit following .editorconfig and the development conventions
4. Open a Pull Request

---

## License

[MIT](LICENSE)
