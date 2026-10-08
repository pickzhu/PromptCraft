using PromptCraft.Interfaces;
using PromptCraft.Data;
using PromptCraft.Models;
using PromptCraft.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PromptCraft.Test;

[TestClass]
public sealed class ProviderServicePersistenceTests
{
    private ServiceProvider? _sp;
    private string? _dbFile;

    [TestInitialize]
    public void Init()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"promptcraft_prov_{Guid.NewGuid():N}.db");
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

    [TestMethod]
    public async Task SaveTwoNewProviders_BothPersist()
    {
        var svc = (ProviderService)_sp!.GetRequiredService<IProviderService>();
        var a = new ProviderConfig { Name = "ProviderA", BaseUrl = "https://a/v1", ApiKey = "k" };
        await svc.SaveAsync(a, default);
        var b = new ProviderConfig { Name = "ProviderB", BaseUrl = "https://b/v1", ApiKey = "k" };
        await svc.SaveAsync(b, default);

        var all = await svc.GetAllAsync(default);
        Assert.AreEqual(2, all.Count);
    }
}
