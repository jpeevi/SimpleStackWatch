using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace MonitorDashboard.Auth;
public static class UserCommands
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (args.Length < 2 || args[0] != "users") return Help();
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DashboardUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        try
        {
            if (args[1] == "list" && args.Length == 2)
            {
                foreach (var u in await users.Users.OrderBy(u => u.UserName).ToListAsync())
                    Console.WriteLine($"{u.UserName}\t{(u.Enabled ? "enabled" : "disabled")}\tMFA: {u.TwoFactorEnabled}");
                return 0;
            }
            if (args[1] == "add" && args.Length <= 3)
            {
                var name = args.Length == 3 ? args[2] : Read("Username: ");
                var password = Password();
                await using var tx = await db.Database.BeginTransactionAsync();
                var u = new DashboardUser { UserName = name, Enabled = false };
                Check(await users.CreateAsync(u, password));
                await EnrollAsync(users, u);
                u.Enabled = true;
                Check(await users.UpdateAsync(u));
                await tx.CommitAsync();
                Console.WriteLine("Account created. Sign in with your password and authenticator code.");
                return 0;
            }
            if (args.Length != 3) return Help();
            var user = await users.FindByNameAsync(args[2]) ?? throw new InvalidOperationException("User not found.");
            switch (args[1])
            {
                case "reset-password":
                    var password = Password();
                    Check(await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password));
                    Check(await users.UpdateSecurityStampAsync(user));
                    Console.WriteLine("Password reset. MFA remains enabled; existing sessions are invalidated.");
                    break;
                case "reset-mfa":
                    await using (var tx = await db.Database.BeginTransactionAsync())
                    {
                        await EnrollAsync(users, user);
                        Check(await users.UpdateSecurityStampAsync(user));
                        await tx.CommitAsync();
                    }
                    Console.WriteLine("MFA reset. Old authenticator keys and recovery codes no longer work.");
                    break;
                case "disable":
                    user.Enabled = false;
                    Check(await users.UpdateAsync(user));
                    Check(await users.UpdateSecurityStampAsync(user));
                    Console.WriteLine("Account disabled; existing sessions are invalidated.");
                    break;
                case "enable":
                    if (!user.TwoFactorEnabled) throw new InvalidOperationException("Enroll MFA before enabling this account.");
                    user.Enabled = true;
                    Check(await users.UpdateAsync(user));
                    Check(await users.UpdateSecurityStampAsync(user));
                    Console.WriteLine("Account enabled.");
                    break;
                case "unlock":
                    Check(await users.SetLockoutEndDateAsync(user, null));
                    Check(await users.ResetAccessFailedCountAsync(user));
                    Console.WriteLine("Account unlocked.");
                    break;
                default: return Help();
            }
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
    private static async Task EnrollAsync(UserManager<DashboardUser> users, DashboardUser user)
    {
        Check(await users.ResetAuthenticatorKeyAsync(user));
        var key = await users.GetAuthenticatorKeyAsync(user);
        Console.WriteLine($"Google Authenticator → Enter a setup key → Time based.\nAccount: MonitorDashboard:{user.UserName}\nSetup key: {key}");
        var verified = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var code = Read("Six-digit authenticator code (or Ctrl+C to abort): ").Replace(" ", "");
            if (await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)) { verified = true; break; }
            Console.Error.WriteLine("Invalid code. Check the server and phone clocks.");
        }
        if (!verified) throw new InvalidOperationException("Enrollment aborted. Changes have been rolled back.");
        Check(await users.SetTwoFactorEnabledAsync(user, true));
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10)
            ?? throw new InvalidOperationException("Could not generate recovery codes.");
        Console.WriteLine("Save these single-use recovery codes privately:");
        foreach (var code in codes) Console.WriteLine(code);
    }
    private static string Password()
    {
        var value = ReadSecret("New password (12+ characters): ");
        if (value != ReadSecret("Confirm password: ")) throw new InvalidOperationException("Passwords do not match.");
        return value;
    }
    private static string Read(string prompt)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Run account commands in an interactive terminal.");
        Console.Write(prompt);
        return Console.ReadLine()?.Trim() ?? throw new OperationCanceledException("Input closed.");
    }
    private static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Run account commands in an interactive terminal.");
        Console.Write(prompt);
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
    private static int Help()
    {
        Console.Error.WriteLine("Usage: users add [username] | list | reset-password <username> | reset-mfa <username> | disable <username> | enable <username> | unlock <username>");
        return 2;
    }
}
