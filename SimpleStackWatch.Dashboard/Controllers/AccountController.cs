using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MonitorDashboard.Auth;
namespace MonitorDashboard.Controllers;
[EnableRateLimiting("login")]
public sealed class AccountController(SignInManager<DashboardUser> signIn, UserManager<DashboardUser> users) : Controller
{
    [AllowAnonymous, HttpGet("/account/login")]
    public IActionResult Login() => User.Identity?.IsAuthenticated == true ? Redirect("/") : View();

    [AllowAnonymous, HttpPost("/account/login")]
    public async Task<IActionResult> Login(string username, string password)
    {
        var user = await users.FindByNameAsync(username ?? "");
        if (user is not { Enabled: true, TwoFactorEnabled: true }) return Failure("Login", "Invalid login.");
        var result = await signIn.PasswordSignInAsync(user, password ?? "", false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
        {
            // Authentication reads the incoming request cookie, not the
            // Set-Cookie header just written by PasswordSignInAsync.
            // Build the pending principal directly after the password check.
            var pending = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
            pending.AddClaim(new Claim(ClaimTypes.Name, await users.GetUserIdAsync(user)));
            var stamp = await users.GetSecurityStampAsync(user);
            pending.AddClaim(new Claim("PendingStamp", stamp));
            pending.AddClaim(new Claim(signIn.Options.ClaimsIdentity.SecurityStampClaimType, stamp));

            await HttpContext.SignInAsync(
                IdentityConstants.TwoFactorUserIdScheme,
                new ClaimsPrincipal(pending),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = false,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                });
            return Redirect("/account/verify");
        }
        // All accounts must pass MFA. Never accept a password-only result.
        await signIn.SignOutAsync();
        return Failure("Login", "Invalid login or account temporarily locked.");
    }
    [AllowAnonymous, HttpGet("/account/verify")]
    public async Task<IActionResult> Verify() => await signIn.GetTwoFactorAuthenticationUserAsync() is null ? Redirect("/account/login") : View();

    [AllowAnonymous, HttpPost("/account/verify")]
    public async Task<IActionResult> Verify(string code, bool recovery = false)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is not { Enabled: true, TwoFactorEnabled: true }) return Redirect("/account/login");
        var pending = await HttpContext.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        if (!pending.Succeeded ||
            string.IsNullOrEmpty(user.SecurityStamp) ||
            pending.Principal?.FindFirst("PendingStamp")?.Value != user.SecurityStamp)
        {
            await HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
            return Redirect("/account/login");
        }
        if (await users.IsLockedOutAsync(user)) return Failure("Verify", "Account temporarily locked.");
        var normalized = (code ?? "").Replace(" ", "");
        var result = recovery
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(normalized)
            : await signIn.TwoFactorAuthenticatorSignInAsync(normalized.Replace("-", ""), false, false);
        if (result.Succeeded) return Redirect("/");
        // Recovery-code failures do not automatically increment Identity's lockout counter.
        if (recovery) await users.AccessFailedAsync(user);
        return Failure("Verify", "Invalid code or account temporarily locked.");
    }
    [Authorize, HttpPost("/account/logout")]
    public async Task<IActionResult> Logout() { await signIn.SignOutAsync(); return Redirect("/account/login"); }
    private IActionResult Failure(string view, string message) { ViewData["Error"] = message; return View(view); }
}
