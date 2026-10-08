using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace PromptCraft.Data;

/// <summary>
/// ComfyDbContext 的轻量 schema 升级。
/// 项目使用 EnsureCreated（无 EF 迁移），它只对"新库"建表，已存在的库不会自动加列/建表。
/// 这里用 PRAGMA 检查缺失列并手动 ALTER TABLE / CREATE TABLE，保证老库升级后 EF 查询新属性不报错。
/// 注意：EF 的表名取自 DbSet 属性名（ImageMetadata / Workflows / ImagePrompts / Folders / Prompts…）。
/// 幂等：重复调用安全。所有表/列一律大驼峰。
/// </summary>
public static class ComfyDbMigrator
{
    private const string ImageTable = "\"ImageMetadata\"";
    private const string WorkflowTable = "\"Workflows\"";
    private const string ImagePromptTable = "\"ImagePrompts\"";

    /// <summary>ImageMetadata 需要补齐的列（老库逐列 ALTER）</summary>
    private static readonly (string Column, string Type)[] RequiredImageInfoColumns =
    {
        ("WorkflowId", "INTEGER"),
        ("PromptId", "INTEGER"),
    };

    /// <summary>确保数据库已创建且 schema 已升级到当前模型版本。所有入口（同步、画廊加载）先调用它。</summary>
    public static async Task EnsureSchemaAsync(ComfyDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await UpgradeImageInfoSchemaAsync(db, ct);
        await EnsureImagePromptTablesAsync(db, ct);
        await EnsureWorkflowSchemaAsync(db, ct);
        await EnsureWorkflowParamsSchemaAsync(db, ct);
        await EnsureWorkflowTagsSchemaAsync(db, ct);
        await EnsureTagColorSchemaAsync(db, ct);
        await EnsureProvidersTablesAsync(db, ct);
        await MigrateProvidersFromSettingsDbAsync(db, ct);
        await EnsureAppSettingsTableAsync(db, ct);
        await EnsurePeProfilesTableAsync(db, ct);
        await EnsureFoldersSchemaAsync(db, ct);
        await EnsurePromptsSchemaAsync(db, ct);
        await EnsureFolderMapsSchemaAsync(db, ct);
    }

    /// <summary>老库没有 Folders 表（统一单级文件夹），手工幂等建。新库由 EnsureCreated 建好。</summary>
    private static async Task EnsureFoldersSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "Folders" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Folders" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "Scope" TEXT NOT NULL DEFAULT 'Prompt',
                    "Sort" INTEGER NOT NULL DEFAULT 0,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "AK_Folders_Scope_Name" UNIQUE ("Scope", "Name")
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);

            await using (var cmd = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_Folders_Scope\" ON \"Folders\"(\"Scope\")"))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    /// <summary>资产-文件夹多对多关联表（PromptFolderMaps / WorkflowFolderMaps / GalleryFolderMaps）；老库幂等建，新库由 EnsureCreated 建好。</summary>
    private static async Task EnsureFolderMapsSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "PromptFolderMaps" (
                    "PromptId" TEXT NOT NULL,
                    "FolderId" INTEGER NOT NULL,
                    CONSTRAINT "PK_PromptFolderMaps" PRIMARY KEY ("PromptId", "FolderId"),
                    CONSTRAINT "FK_PromptFolderMaps_Prompts_PromptId" FOREIGN KEY ("PromptId") REFERENCES "Prompts" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_PromptFolderMaps_Folders_FolderId" FOREIGN KEY ("FolderId") REFERENCES "Folders" ("Id") ON DELETE CASCADE
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);
            await using (var cmd = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_PromptFolderMaps_FolderId\" ON \"PromptFolderMaps\"(\"FolderId\")"))
                await cmd.ExecuteNonQueryAsync(ct);

            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "WorkflowFolderMaps" (
                    "WorkflowId" INTEGER NOT NULL,
                    "FolderId" INTEGER NOT NULL,
                    CONSTRAINT "PK_WorkflowFolderMaps" PRIMARY KEY ("WorkflowId", "FolderId"),
                    CONSTRAINT "FK_WorkflowFolderMaps_Workflows_WorkflowId" FOREIGN KEY ("WorkflowId") REFERENCES "Workflows" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_WorkflowFolderMaps_Folders_FolderId" FOREIGN KEY ("FolderId") REFERENCES "Folders" ("Id") ON DELETE CASCADE
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);
            await using (var cmd = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_WorkflowFolderMaps_FolderId\" ON \"WorkflowFolderMaps\"(\"FolderId\")"))
                await cmd.ExecuteNonQueryAsync(ct);

            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "GalleryFolderMaps" (
                    "ImageInfoId" INTEGER NOT NULL,
                    "FolderId" INTEGER NOT NULL,
                    CONSTRAINT "PK_GalleryFolderMaps" PRIMARY KEY ("ImageInfoId", "FolderId"),
                    CONSTRAINT "FK_GalleryFolderMaps_ImageMetadata_ImageInfoId" FOREIGN KEY ("ImageInfoId") REFERENCES "ImageMetadata" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_GalleryFolderMaps_Folders_FolderId" FOREIGN KEY ("FolderId") REFERENCES "Folders" ("Id") ON DELETE CASCADE
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);
            await using (var cmd = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_GalleryFolderMaps_FolderId\" ON \"GalleryFolderMaps\"(\"FolderId\")"))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    /// <summary>老库没有 Prompts / PromptTagMaps 表（提示词库已并入本库），手工幂等建。新库由 EnsureCreated 建好。</summary>
    private static async Task EnsurePromptsSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "Prompts" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Prompts" PRIMARY KEY,
                    "Title" TEXT NULL,
                    "Positive" TEXT NULL,
                    "Negative" TEXT NULL,
                    "Cover" TEXT NULL,
                    "Note" TEXT NULL,
                    "Seed" TEXT NULL,
                    "Models" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);

            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "PromptTagMaps" (
                    "PromptId" TEXT NOT NULL,
                    "TagId" TEXT NOT NULL,
                    CONSTRAINT "PK_PromptTagMaps" PRIMARY KEY ("PromptId", "TagId"),
                    CONSTRAINT "FK_PromptTagMaps_Prompts_PromptId" FOREIGN KEY ("PromptId") REFERENCES "Prompts" ("Id") ON DELETE CASCADE
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);

            await using (var cmd = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_PromptTagMaps_TagId\" ON \"PromptTagMaps\"(\"TagId\")"))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    /// <summary>老库没有 PromptEngineeringProfiles 表（自定义提示词工程），手工幂等建。新库由 EnsureCreated 建好。</summary>
    private static async Task EnsurePeProfilesTableAsync(ComfyDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""PromptEngineeringProfiles"" (
            ""Id"" TEXT NOT NULL CONSTRAINT ""PK_PromptEngineeringProfiles"" PRIMARY KEY,
            ""Kind"" TEXT NULL,
            ""Builtin"" INTEGER NULL,
            ""BuiltinKey"" TEXT NULL,
            ""CaptionType"" TEXT NULL,
            ""StructuredFormat"" INTEGER NULL,
            ""ToriiFormat"" INTEGER NULL,
            ""ToriiExtractMode"" TEXT NULL,
            ""ToriiUseNamesDefault"" INTEGER NULL,
            ""Name"" TEXT NULL,
            ""Category"" TEXT NULL,
            ""Description"" TEXT NULL,
            ""Enabled"" INTEGER NOT NULL DEFAULT 1,
            ""Sort"" INTEGER NOT NULL DEFAULT 0,
            ""OutputFormat"" TEXT NULL,
            ""Tags"" TEXT NULL,
            ""SubjectDomains"" TEXT NULL,
            ""SystemPrompt"" TEXT NULL,
            ""UserPromptTemplate"" TEXT NULL,
            ""CreatedAt"" INTEGER NOT NULL DEFAULT 0,
            ""UpdatedAt"" INTEGER NOT NULL DEFAULT 0)", ct);
        await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_PromptEngineeringProfiles_Kind"" ON ""PromptEngineeringProfiles""(""Kind"")", ct);
    }

    /// <summary>老 comfy.db 没有 AppSettings 表（全局业务 KV），手工幂等建。新库由 EnsureCreated 建好。</summary>
    private static async Task EnsureAppSettingsTableAsync(ComfyDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""AppSettings"" (
            ""Key"" TEXT NOT NULL CONSTRAINT ""PK_AppSettings"" PRIMARY KEY,
            ""Value"" TEXT NOT NULL DEFAULT '')", ct);
    }

    /// <summary>老 comfy.db 没有 provider 表，手工幂等建（表名大驼峰）。</summary>
    private static async Task EnsureProvidersTablesAsync(ComfyDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""ProviderConfigs"" (
            ""Id"" TEXT NOT NULL CONSTRAINT ""PK_ProviderConfigs"" PRIMARY KEY,
            ""Name"" TEXT NOT NULL,
            ""BaseUrl"" TEXT NOT NULL,
            ""ApiKey"" TEXT NOT NULL,
            ""Enabled"" INTEGER NOT NULL,
            ""Sort"" INTEGER NOT NULL,
            ""CreatedAt"" TEXT NOT NULL,
            ""UpdatedAt"" TEXT NOT NULL)", ct);
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""ProviderModels"" (
            ""Id"" TEXT NOT NULL CONSTRAINT ""PK_ProviderModels"" PRIMARY KEY,
            ""ProviderId"" TEXT NOT NULL,
            ""ModelName"" TEXT NOT NULL,
            ""UseForExpand"" INTEGER NOT NULL,
            ""UseForReverse"" INTEGER NOT NULL,
            ""IsDefaultExpand"" INTEGER NOT NULL,
            ""IsDefaultReverse"" INTEGER NOT NULL)", ct);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_ProviderModels_ProviderId\" ON \"ProviderModels\" (\"ProviderId\")", ct);
    }

    /// <summary>
    /// T0.7 迁移：把 settings.db 里残留的 provider_configs/provider_models 数据搬到 comfy.db。
    /// 搬完后 settings.db 里的表由 ConfigRepository.EnsureDatabase DROP 掉。
    /// 幂等：comfy.db 已有 provider 数据则跳过。
    /// </summary>
    private static async Task MigrateProvidersFromSettingsDbAsync(ComfyDbContext db, CancellationToken ct)
    {
        // comfy.db 已有 provider 数据 → 跳过
        if (await db.ProviderConfigs.AnyAsync(ct)) return;

        var settingsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PromptCraft", "settings.db");
        if (!System.IO.File.Exists(settingsPath)) return;

        using var settingsConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={settingsPath}");
        await settingsConn.OpenAsync(ct);

        // 检查 settings.db 是否还有 provider_configs 表
        using (var check = settingsConn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='provider_configs'";
            var exists = await check.ExecuteScalarAsync(ct);
            if (exists == null) return;
        }

        // 读 providers
        var providers = new List<(string Id, string Name, string BaseUrl, string ApiKey, bool Enabled, int Sort, DateTime CreatedAt, DateTime UpdatedAt)>();
        using (var cmd = settingsConn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, BaseUrl, ApiKey, Enabled, Sort, CreatedAt, UpdatedAt FROM provider_configs";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                providers.Add((
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetInt32(4) != 0, r.GetInt32(5),
                    r.GetDateTime(6), r.GetDateTime(7)));
            }
        }
        if (providers.Count == 0) return;

        // 读 models
        var models = new List<(string Id, string ProviderId, string ModelName, bool UseForExpand, bool UseForReverse, bool IsDefaultExpand, bool IsDefaultReverse)>();
        using (var cmd = settingsConn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, ProviderId, ModelName, UseForExpand, UseForReverse, IsDefaultExpand, IsDefaultReverse FROM provider_models";
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                models.Add((
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.GetInt32(3) != 0, r.GetInt32(4) != 0,
                    r.GetInt32(5) != 0, r.GetInt32(6) != 0));
            }
        }

        // 写入 comfy.db
        foreach (var p in providers)
        {
            db.ProviderConfigs.Add(new Models.ProviderConfig
            {
                Id = p.Id, Name = p.Name, BaseUrl = p.BaseUrl, ApiKey = p.ApiKey,
                Enabled = p.Enabled, Sort = p.Sort,
                CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt,
            });
        }
        foreach (var m in models)
        {
            db.ProviderModels.Add(new Models.ProviderModel
            {
                Id = m.Id, ProviderId = m.ProviderId, ModelName = m.ModelName,
                UseForExpand = m.UseForExpand, UseForReverse = m.UseForReverse,
                IsDefaultExpand = m.IsDefaultExpand, IsDefaultReverse = m.IsDefaultReverse,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Tags 表补 Color 列（标签颜色，hex 字符串），并为老库中无颜色的标签一次性回填随机调色板颜色。
    /// 幂等：已有颜色列/已有颜色的标签跳过。
    /// </summary>
    private static async Task EnsureTagColorSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            await AddColumnIfMissingAsync(connection, "\"Tags\"", "Color", "TEXT NULL", ct);

            // 回填：仅处理 Color 为空/缺省的旧标签，逐条随机取色并尽量避重
            var usedColors = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var read = CreateCommand(connection,
                "SELECT \"Color\" FROM \"Tags\" WHERE \"Color\" IS NOT NULL AND \"Color\" <> ''"))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var color = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(color)) usedColors.Add(color);
                }
            }

            var missingIds = new System.Collections.Generic.List<int>();
            await using (var read = CreateCommand(connection,
                "SELECT \"Id\" FROM \"Tags\" WHERE \"Color\" IS NULL OR \"Color\" = ''"))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    missingIds.Add(reader.GetInt32(0));
            }

            foreach (var id in missingIds)
            {
                var hex = PromptCraft.Models.ComfyUI.TagPalette.PickDistinctColorHex(usedColors);
                usedColors.Add(hex);
                await using var update = CreateCommand(connection,
                    "UPDATE \"Tags\" SET \"Color\" = @color WHERE \"Id\" = @id");
                var pColor = update.CreateParameter();
                pColor.ParameterName = "@color";
                pColor.Value = hex;
                update.Parameters.Add(pColor);
                var pId = update.CreateParameter();
                pId.ParameterName = "@id";
                pId.Value = id;
                update.Parameters.Add(pId);
                await update.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    /// <summary>建 WorkflowTags 表（工作流-标签多对多，与图库共用 Tag 表；老库升级用 CREATE TABLE IF NOT EXISTS）。</summary>
    private static async Task EnsureWorkflowTagsSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "WorkflowTags" (
                    "WorkflowId" INTEGER NOT NULL,
                    "TagId" INTEGER NOT NULL,
                    CONSTRAINT "PK_WorkflowTags" PRIMARY KEY ("WorkflowId", "TagId"),
                    CONSTRAINT "FK_WorkflowTags_Tags_TagId" FOREIGN KEY ("TagId") REFERENCES "Tags" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_WorkflowTags_Workflows_WorkflowId" FOREIGN KEY ("WorkflowId") REFERENCES "Workflows" ("Id") ON DELETE CASCADE
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);

            // 按标签筛选工作流（列表页标签筛选走 TagId 索引）
            await using (var cmd = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_WorkflowTags_TagId\" ON \"WorkflowTags\"(\"TagId\")"))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    /// <summary>建 WorkflowParams 表（参数配置；老库升级用 CREATE TABLE IF NOT EXISTS，新库 EnsureCreated 已建）。</summary>
    private static async Task EnsureWorkflowParamsSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            await using (var cmd = CreateCommand(connection,
                """
                CREATE TABLE IF NOT EXISTS "WorkflowParams" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkflowParams" PRIMARY KEY AUTOINCREMENT,
                    "WorkflowId" INTEGER NOT NULL,
                    "ParamsJson" TEXT NOT NULL DEFAULT '[]',
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_WorkflowParams_Workflows_WorkflowId" FOREIGN KEY ("WorkflowId") REFERENCES "Workflows" ("Id") ON DELETE CASCADE
                )
                """))
                await cmd.ExecuteNonQueryAsync(ct);

            await using (var cmd = CreateCommand(connection,
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_WorkflowParams_WorkflowId\" ON \"WorkflowParams\"(\"WorkflowId\")"))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    /// <summary>老库补列：Workflows / WorkflowInputs / WorkflowJobs / JobOutputs 的逻辑删除与同步字段。</summary>
    private static async Task EnsureWorkflowSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            await AddColumnIfMissingAsync(connection, WorkflowTable, "SourcePath", "TEXT", ct);
            await AddColumnIfMissingAsync(connection, WorkflowTable, "IsDeleted", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddColumnIfMissingAsync(connection, WorkflowTable, "Source", "TEXT NOT NULL DEFAULT 'user'", ct);
            await AddColumnIfMissingAsync(connection, WorkflowTable, "WorkflowGuid", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(connection, WorkflowTable, "NodeCount", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddColumnIfMissingAsync(connection, WorkflowTable, "WorkflowJsonBlob", "BLOB NULL", ct);
            await AddColumnIfMissingAsync(connection, "\"WorkflowInputs\"", "IsDeleted", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddColumnIfMissingAsync(connection, "\"WorkflowJobs\"", "IsDeleted", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddColumnIfMissingAsync(connection, "\"WorkflowJobs\"", "StartedAt", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(connection, "\"WorkflowJobs\"", "NodeLogBlob", "BLOB NULL", ct);
            await AddColumnIfMissingAsync(connection, "\"JobOutputs\"", "IsDeleted", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddColumnIfMissingAsync(connection, "\"JobOutputs\"", "PromptId", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(connection, ImagePromptTable, "IsDeleted", "INTEGER NOT NULL DEFAULT 0", ct);

            // Workflows.SourcePath 唯一（部分索引：NULL 不参与），作为用户工作流同步去重键
            await using (var cmd = CreateCommand(connection,
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Workflow_SourcePath\" ON \"Workflows\"(\"SourcePath\") WHERE \"SourcePath\" IS NOT NULL"))
                await cmd.ExecuteNonQueryAsync(ct);

            // Workflows.WorkflowGuid 唯一（图片提取工作流去重键：ComfyUI 顶级 UUID）
            await using (var cmd = CreateCommand(connection,
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Workflow_WorkflowGuid\" ON \"Workflows\"(\"WorkflowGuid\") WHERE \"WorkflowGuid\" IS NOT NULL"))
                await cmd.ExecuteNonQueryAsync(ct);

            // 列表过滤索引
            await using (var cmd = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_Workflow_IsDeleted\" ON \"Workflows\"(\"IsDeleted\")"))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    private static async Task AddColumnIfMissingAsync(DbConnection connection, string table, string column, string type, CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = CreateCommand(connection, $"PRAGMA table_info({table})"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetString(1));
        }

        if (existing.Contains(column)) return;
        await using var addCmd = CreateCommand(connection,
            $"ALTER TABLE {table} ADD COLUMN \"{column}\" {type}");
        await addCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>给 ImageMetadata 补列 + 建索引（老库专用；新库由 EnsureCreated 建好）</summary>
    private static async Task UpgradeImageInfoSchemaAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = CreateCommand(connection, $"PRAGMA table_info({ImageTable})"))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    existing.Add(reader.GetString(1)); // 第 2 列为列名
            }

            foreach (var (column, type) in RequiredImageInfoColumns)
            {
                if (existing.Contains(column)) continue;
                await using var cmd = CreateCommand(connection,
                    $"ALTER TABLE {ImageTable} ADD COLUMN \"{column}\" {type} NULL");
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // ImageMetadata 上的关联索引
            foreach (var column in new[] { "WorkflowId", "PromptId" })
            {
                await using var idxCmd = CreateCommand(connection,
                    $"CREATE INDEX IF NOT EXISTS \"IX_ImageInfo_{column}\" ON {ImageTable}(\"{column}\")");
                await idxCmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// 老库补建 ImagePrompts 表与唯一索引（提示词去重键）。工作流已并入 Workflows 表，不再建独立表。
    /// 兼容旧模型遗留：老库的 ImagePrompts 外键曾指向已废弃的 ImageWorkflows 表；
    /// 检测到这种外键时重建表（SQLite 不支持 ALTER FK），指向 Workflows，否则新代码写入会 FK 失败。
    /// </summary>
    private static async Task EnsureImagePromptTablesAsync(ComfyDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool needClose = connection.State != System.Data.ConnectionState.Open;
        if (needClose) await connection.OpenAsync(ct);
        try
        {
            bool tableExists = await HasTableAsync(connection, "ImagePrompts", ct);

            // 旧模型遗留：外键指向 ImageWorkflows → 整表重建（DROP 后按新模型建，FK 指向 Workflows）
            if (tableExists && await GetFkTargetsAsync(connection, "ImagePrompts", ct) is { } targets
                && targets.Any(t => string.Equals(t, "ImageWorkflows", StringComparison.OrdinalIgnoreCase)))
            {
                await using (var off = CreateCommand(connection, "PRAGMA foreign_keys=OFF"))
                    await off.ExecuteNonQueryAsync(ct);
                try
                {
                    await using (var drop = CreateCommand(connection, $"DROP TABLE {ImagePromptTable}"))
                        await drop.ExecuteNonQueryAsync(ct);
                    await CreateImagePromptsTableAsync(connection, ct);
                    await CreateImagePromptIndexesAsync(connection, ct);
                }
                finally
                {
                    await using (var on = CreateCommand(connection, "PRAGMA foreign_keys=ON"))
                        await on.ExecuteNonQueryAsync(ct);
                }
            }
            else
            {
                await CreateImagePromptsTableAsync(connection, ct);
                await CreateImagePromptIndexesAsync(connection, ct);
            }
        }
        finally
        {
            if (needClose) await connection.CloseAsync();
        }
    }

    private static async Task CreateImagePromptsTableAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, $"""
            CREATE TABLE IF NOT EXISTS {ImagePromptTable} (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ImagePrompts" PRIMARY KEY AUTOINCREMENT,
                "WorkflowId" INTEGER NOT NULL,
                "PromptHash" TEXT NOT NULL,
                "PromptJsonBlob" BLOB NULL,
                "PositivePrompt" TEXT NULL,
                "NegativePrompt" TEXT NULL,
                "Model" TEXT NULL,
                "Seed" TEXT NULL,
                "Steps" TEXT NULL,
                "Cfg" TEXT NULL,
                "Sampler" TEXT NULL,
                "Scheduler" TEXT NULL,
                "Width" INTEGER NULL,
                "Height" INTEGER NULL,
                "LoraNames" TEXT NULL,
                "NodesJsonBlob" BLOB NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ImagePrompts_Workflows_WorkflowId"
                    FOREIGN KEY ("WorkflowId") REFERENCES {WorkflowTable} ("Id")
                    ON DELETE CASCADE
            )
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateImagePromptIndexesAsync(DbConnection connection, CancellationToken ct)
    {
        // 同一工作流内 PromptHash 唯一 → 去重复用
        await using (var cmd = CreateCommand(connection, $"""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImagePrompt_WorkflowId_PromptHash"
                ON {ImagePromptTable}("WorkflowId", "PromptHash")
            """))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = CreateCommand(connection,
            $"CREATE INDEX IF NOT EXISTS \"IX_ImagePrompt_WorkflowId\" ON {ImagePromptTable}(\"WorkflowId\")"))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HasTableAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection,
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\"='table' AND \"name\"=@name");
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = table;
        cmd.Parameters.Add(p);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0) > 0;
    }

    /// <summary>返回表的所有外键目标表名（PRAGMA foreign_key_list）。</summary>
    private static async Task<List<string>> GetFkTargetsAsync(DbConnection connection, string table, CancellationToken ct)
    {
        var result = new List<string>();
        await using var cmd = CreateCommand(connection, $"PRAGMA foreign_key_list(\"{table}\")");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(2)); // 第 3 列为被引用表名
        return result;
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }
}
