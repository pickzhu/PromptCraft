using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PromptCraft.Data
{
    // IDesignTimeDbContextFactory 是 EF Core 的“设计时”约定。
    // 当工具（如 MSBuild 任务或 dotnet ef CLI）需要创建你的 DbContext 时，
    // 它们会寻找并自动使用这个工厂类。
    public class ComfyDbContextFactory : IDesignTimeDbContextFactory<ComfyDbContext>
    {
        public ComfyDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ComfyDbContext>();
            optionsBuilder.UseSqlite("Data Source=design_time.db");
            return new ComfyDbContext(optionsBuilder.Options);
        }
    }
}
