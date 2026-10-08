using PromptCraft.Interfaces;
using PromptCraft.Data;
using PromptCraft.Models;
using PromptCraft.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PromptCraft.Test;

[TestClass]
public sealed class ProviderServiceTests
{
    private ServiceProvider? _sp;
    private string? _dbFile;

    [TestInitialize]
    public void Init()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"promptcraft_providers_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComfyDbContext>(o => o.UseSqlite($"Data Source={_dbFile}"));
        services.AddSingleton<IProviderService, ProviderService>();
        _sp = services.BuildServiceProvider();

        using var db = _sp.GetRequiredService<IDbContextFactory<ComfyDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _sp?.Dispose();
        if (_dbFile != null)
        {
            try { if (File.Exists(_dbFile)) File.Delete(_dbFile); } catch { }
        }
    }

    private ProviderService Svc => (ProviderService)_sp!.GetRequiredService<IProviderService>();

    [TestMethod]
    public async Task Save_GetById_RoundTrip_WithModelFlags()
    {
        var svc = Svc;
        var p = new ProviderConfig
        {
            Name = "CherryIN",
            BaseUrl = "https://open.cherryin.net/v1",
            ApiKey = "sk-original",
            Enabled = true,
            Models =
            {
                new ProviderModel { ModelName = "deepseek-chat", UseForExpand = true, IsDefaultExpand = true },
                new ProviderModel { ModelName = "glm-4v", UseForReverse = true, IsDefaultReverse = true },
            },
        };
        await svc.SaveAsync(p, default);

        var loaded = await svc.GetByIdAsync(p.Id, default);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded!.Models.Count);
        var expand = loaded.Models.First(m => m.ModelName == "deepseek-chat");
        Assert.IsTrue(expand.UseForExpand);
        Assert.IsTrue(expand.IsDefaultExpand);
        var rev = loaded.Models.First(m => m.ModelName == "glm-4v");
        Assert.IsTrue(rev.UseForReverse);
        Assert.IsTrue(rev.IsDefaultReverse);
    }

    [TestMethod]
    public async Task Save_EmptyApiKey_DoesNotOverwriteExisting()
    {
        var svc = Svc;
        var p = new ProviderConfig { Name = "P", BaseUrl = "https://x/v1", ApiKey = "sk-original" };
        await svc.SaveAsync(p, default);

        var edited = new ProviderConfig { Id = p.Id, Name = "P2", BaseUrl = "https://x/v1", ApiKey = "" };
        await svc.SaveAsync(edited, default);

        var loaded = await svc.GetByIdAsync(p.Id, default);
        Assert.AreEqual("sk-original", loaded!.ApiKey);
        Assert.AreEqual("P2", loaded.Name);
    }

    [TestMethod]
    public async Task GetDefaultReverse_Null_WhenNoModelMarked()
    {
        var svc = Svc;
        Assert.IsNull(await svc.GetDefaultReverseAsync(default));

        var p = new ProviderConfig { Name = "TextOnly", BaseUrl = "https://x/v1", ApiKey = "k", Enabled = true };
        p.Models.Add(new ProviderModel { ModelName = "m", UseForExpand = true });
        await svc.SaveAsync(p, default);
        Assert.IsNull(await svc.GetDefaultReverseAsync(default));
    }

    [TestMethod]
    public async Task GetDefaultReverse_Returns_MarkedModel()
    {
        var svc = Svc;
        var p = new ProviderConfig { Name = "GLM", BaseUrl = "https://open.bigmodel.cn/api/paas/v4", ApiKey = "k", Enabled = true };
        p.Models.Add(new ProviderModel { ModelName = "glm-4v", UseForReverse = true, IsDefaultReverse = true });
        await svc.SaveAsync(p, default);

        var res = await svc.GetDefaultReverseAsync(default);
        Assert.IsNotNull(res);
        Assert.AreEqual("glm-4v", res!.Value.Item2.ModelName);
    }

    [TestMethod]
    public async Task Save_AutoDefaultsFirstExpandable_WhenNoDefaultFlagged()
    {
        var svc = Svc;
        var p = new ProviderConfig { Name = "Auto", BaseUrl = "https://x/v1", ApiKey = "k", Enabled = true };
        p.Models.Add(new ProviderModel { ModelName = "m1", UseForExpand = true });
        p.Models.Add(new ProviderModel { ModelName = "m2", UseForExpand = true });
        await svc.SaveAsync(p, default);

        var loaded = await svc.GetByIdAsync(p.Id, default);
        Assert.IsTrue(loaded!.Models.First(m => m.ModelName == "m1").IsDefaultExpand);
        Assert.IsFalse(loaded.Models.First(m => m.ModelName == "m2").IsDefaultExpand);
    }

    [TestMethod]
    public async Task GetDefaultExpand_FallsBackToFirstExpandable_WhenNoDefault()
    {
        var svc = Svc;
        var p = new ProviderConfig { Name = "FB", BaseUrl = "https://x/v1", ApiKey = "k", Enabled = true };
        p.Models.Add(new ProviderModel { ModelName = "text-only", UseForExpand = true, UseForReverse = false });
        await svc.SaveAsync(p, default);

        var res = await svc.GetDefaultExpandAsync(default);
        Assert.IsNotNull(res);
        Assert.AreEqual("text-only", res!.Value.Item2.ModelName);
    }

    [TestMethod]
    public async Task Delete_RemovesProviderAndModels()
    {
        var svc = Svc;
        var p = new ProviderConfig
        {
            Name = "Tmp", BaseUrl = "https://x/v1", ApiKey = "k",
            Models = { new ProviderModel { ModelName = "m1" } },
        };
        await svc.SaveAsync(p, default);
        await svc.DeleteAsync(p.Id, default);

        Assert.IsNull(await svc.GetByIdAsync(p.Id, default));
        Assert.AreEqual(0, (await svc.GetAllAsync(default)).Count);
    }
}
