using Microsoft.AspNetCore.Identity;
namespace MonitorDashboard.Auth;
public sealed class DashboardUser : IdentityUser
{
    public bool Enabled { get; set; }
}
