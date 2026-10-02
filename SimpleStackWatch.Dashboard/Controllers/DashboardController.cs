using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MonitorDashboard.Models;
namespace MonitorDashboard.Controllers;
[Authorize]
public sealed class DashboardController(DashboardOptions options) : Controller
{
    [HttpGet("/"), HttpGet("/dashboard/{tab}")]
    public IActionResult Index(string tab = "logs")
    {
        if (!new[] { "logs", "processes", "postgres", "redis", "ec2" }.Contains(tab)) return NotFound();
        return View(new DashboardViewModel(tab, Math.Clamp(options.RefreshSeconds, 5, 300), options.Logs.EnableFileViewer, options.Logs.Database.Enabled, options.Processes.Monitor.Enabled, options.Logs.Folders));
    }
}
