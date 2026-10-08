using PromptCraft.Data;
using PromptCraft.Service;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Test;

// 参数持久化验证：comfyui.db AppSettings KV（对齐 PromptMaster settingOperation）
[TestClass]
public sealed class ExpandSettingsStoreTests
{
    private sealed class TestFactory : IDbContextFactory<ComfyDbContext>
    {
        private readonly string _path;
        public TestFactory(string path) => _path = path;
        public ComfyDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ComfyDbContext>()
            .UseSqlite($"Data Source={_path}").Options);
        public Task<ComfyDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private static async Task<(string dir, AppSettingsRepository repo)> CreateEnvAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fn_set_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "comfy.db");
        var factory = new TestFactory(path);
        await using (var db = factory.CreateDbContext())
            await ComfyDbMigrator.EnsureSchemaAsync(db);
        return (dir, new AppSettingsRepository(factory));
    }

    [TestMethod]
    public async Task Set_Flushes_To_Db_And_Reload_Restores()
    {
        var (dir, repo) = await CreateEnvAsync();
        try
        {
            var store = new ExpandSettingsStore(repo);
            await store.LoadAsync();
            store.Set("pm_expand_length", "long");
            store.Set("pm_expand_output_lang", "en");
            store.Set("pm_expand_user_extra_prompt", "细节丰富，电影感");
            await Task.Delay(900); // 等 400ms debounce flush

            Assert.AreEqual("long", await repo.GetAsync("pm_expand_length"));
            Assert.AreEqual("en", await repo.GetAsync("pm_expand_output_lang"));
            Assert.AreEqual("细节丰富，电影感", await repo.GetAsync("pm_expand_user_extra_prompt"));

            // 模拟重启：新 Store LoadAsync 后读到全部记忆
            var store2 = new ExpandSettingsStore(repo);
            await store2.LoadAsync();
            Assert.AreEqual("long", store2.Get("pm_expand_length"));
            Assert.AreEqual("en", store2.Get("pm_expand_output_lang"));
            Assert.AreEqual("细节丰富，电影感", store2.Get("pm_expand_user_extra_prompt"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Upsert_Overwrites_Same_Key()
    {
        var (dir, repo) = await CreateEnvAsync();
        try
        {
            var store = new ExpandSettingsStore(repo);
            await store.LoadAsync();
            store.Set("pm_expand_rule_id", "prose");
            await Task.Delay(900);
            store.Set("pm_expand_rule_id", "danbooru_tags");
            await Task.Delay(900);

            Assert.AreEqual("danbooru_tags", await repo.GetAsync("pm_expand_rule_id"));
            var all = await repo.GetAllAsync();
            Assert.AreEqual(1, all.Count(kv => kv.Key == "pm_expand_rule_id"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Remove_Deletes_Key()
    {
        var (dir, repo) = await CreateEnvAsync();
        try
        {
            var store = new ExpandSettingsStore(repo);
            await store.LoadAsync();
            store.Set("pm_expand_caption_model", "gpt-4o");
            await Task.Delay(900);
            store.Remove("pm_expand_caption_model");
            await Task.Delay(900);

            Assert.IsNull(await repo.GetAsync("pm_expand_caption_model"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
