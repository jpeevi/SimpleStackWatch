using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
namespace MonitorDashboard.Auth;
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options, IDataProtectionProvider protection)
    : IdentityDbContext<DashboardUser>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        var protector = protection.CreateProtector("MonitorDashboard.IdentityTokens.v1");
        builder.Entity<IdentityUserToken<string>>().Property(t => t.Value)
            .HasConversion(v => v == null ? null : protector.Protect(v), v => v == null ? null : protector.Unprotect(v));
    }
}
