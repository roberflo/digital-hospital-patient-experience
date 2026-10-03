using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Recepcion;

public sealed class DesignFactory : IDesignTimeDbContextFactory<CrmDb>
{
    public CrmDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CrmDb>().UseNpgsql("Host=localhost;Database=recepcion;Username=recepcion").Options, new TenantScope(), DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "recepcion-design-keys"))));
}
