global using Microsoft.AspNetCore.Hosting;
global using Microsoft.AspNetCore.Http;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ClickSpares.SparesExplorer.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

var directory = Path.Combine(Path.GetTempPath(), "spares-security-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var checks = 0;
try
{
    var environment = new TestEnvironment(directory);
    var store = new UserStore(environment);
    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    var replacement = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    const string admin = "admin@clickspares.co.uk";
    const string user = "user@clickspares.co.uk";

    Check(AdminAccountScope.IsStaff(" Admin@ClickSpares.co.uk "), "Exact staff domain accepts normalized addresses");
    foreach (var address in new[] { "admin@clickspares.co.uk.evil", "admin@evilclickspares.co.uk", "a@b@clickspares.co.uk", "admin@whitemeadow.com", "admin@thebrandedfurnitureco.com", "@clickspares.co.uk", "clickspares.co.uk" })
        Check(!AdminAccountScope.IsStaff(address), "Lookalike, partner and malformed domains are rejected");
    Check(!AdminAccountScope.CanManage(admin, "someone@whitemeadow.com"), "Staff administrators cannot create external accounts");
    Check(!AdminAccountScope.CanManage("someone@whitemeadow.com", admin), "External administrators cannot manage staff");

    await Reject<ArgumentException>(() => store.CreateFirstAdministratorAsync("admin@external.example", password), "Bootstrap rejects external accounts");
    await store.CreateFirstAdministratorAsync(admin, password);
    var first = await store.AuthenticateAsync(admin, password);
    Check(first is { IsAdmin: true }, "First administrator can sign in");
    Check(await store.IsCurrentAsync(admin, first!.SecurityStamp, true), "Current administrator stamp is valid");
    Check(!await store.IsCurrentAsync(admin, first.SecurityStamp, false), "A changed role cannot reuse an old role claim");
    Check(await store.AuthenticateAsync(admin, replacement) is null, "Incorrect passwords fail");
    Check(await store.AuthenticateAsync("unknown@clickspares.co.uk", password) is null, "Unknown accounts fail");
    var usersPath = Path.Combine(directory, "Data", "Auth", "users.json");
    Check(!(await File.ReadAllTextAsync(usersPath)).Contains(password, StringComparison.Ordinal), "Passwords are never persisted in plaintext");
    await Reject<ArgumentException>(() => store.CreateFirstAdministratorAsync("other@clickspares.co.uk", password), "Bootstrap cannot replace an existing account database");

    await store.CreateForAdminAsync(admin, user, password, false);
    await store.CreateForAdminAsync(admin, "second-admin@clickspares.co.uk", password, true);
    await Reject<ArgumentException>(() => store.CreateForAdminAsync(admin, "external@whitemeadow.com", password, true), "Admin creation rejects external domains");
    await Reject<ArgumentException>(() => store.CreateForAdminAsync(user, "intruder@clickspares.co.uk", password, true), "Non-administrators cannot create privileged accounts");
    await Reject<ArgumentException>(async () => { await store.ListForAdminAsync(user); }, "Non-administrators cannot list accounts");
    await Reject<ArgumentException>(() => store.ResetPasswordForAdminAsync(user, admin, replacement), "Non-administrators cannot reset an administrator password");
    await Reject<ArgumentException>(() => store.SetEnabledForAdminAsync(admin, admin, false), "An administrator cannot disable their own account");
    await Reject<ArgumentException>(() => store.DeleteAsync(admin, admin), "An administrator cannot delete their own account");

    var initialUser = await store.AuthenticateAsync(user, password);
    await store.ResetPasswordForAdminAsync(admin, user, replacement);
    Check(!await store.IsCurrentAsync(user, initialUser!.SecurityStamp), "Password reset revokes the prior session stamp");
    Check(await store.AuthenticateAsync(user, password) is null, "The old password fails after reset");
    var resetUser = await store.AuthenticateAsync(user, replacement);
    Check(resetUser is not null, "The replacement password works");
    await store.SetEnabledForAdminAsync(admin, user, false);
    Check(!await store.IsCurrentAsync(user, resetUser!.SecurityStamp), "Disabling an account revokes its session");
    Check(await store.AuthenticateAsync(user, replacement) is null, "Disabled accounts cannot authenticate");
    await store.SetEnabledForAdminAsync(admin, user, true);
    Check(!await store.IsCurrentAsync(user, resetUser.SecurityStamp), "Re-enabling does not revive a revoked session");
    var reenabled = await store.AuthenticateAsync(user, replacement);
    await store.DeleteAsync(user, admin);
    Check(!await store.IsCurrentAsync(user, reenabled!.SecurityStamp), "Deleting an account revokes its session");
    await store.CreateForAdminAsync(admin, user, password, false);
    Check(!await store.IsCurrentAsync(user, reenabled.SecurityStamp), "Recreating an email does not revive a deleted account session");

    var attempts = Enumerable.Range(0, 2).Select(async _ =>
    {
        try { await store.CreateForAdminAsync(admin, "concurrent@clickspares.co.uk", password, false); return 1; }
        catch (ArgumentException) { return 0; }
    });
    Check((await Task.WhenAll(attempts)).Sum() == 1, "Concurrent creates cannot duplicate an email");
    Check((await store.ListForAdminAsync(admin)).Count(account => account.Email == "concurrent@clickspares.co.uk") == 1,
        "Concurrent mutations preserve a consistent account file");

    var validAccounts = await File.ReadAllTextAsync(usersPath);
    await File.WriteAllTextAsync(usersPath, "{ invalid json");
    await Reject<JsonException>(async () => { await store.AuthenticateAsync(admin, password); }, "Corrupt account files fail closed rather than using cached accounts");
    await File.WriteAllTextAsync(usersPath, validAccounts);
    Check(await store.IsCurrentAsync(admin, first.SecurityStamp, true), "A repaired account file is reread without restart");

    var allowlist = Path.Combine(directory, "Data", "Auth", "admin_allowed_ips.txt");
    var ipAccess = new AdminIpAccess(environment, NullLogger<AdminIpAccess>.Instance);
    var context = new DefaultHttpContext { User = Principal(admin, true, "test") };
    context.Connection.RemoteIpAddress = IPAddress.Loopback;
    Check(!ipAccess.CanManage(context), "A missing admin allowlist denies access");
    await File.WriteAllTextAsync(allowlist, "# Explicit loopback only\n127.0.0.1\n");
    Check(ipAccess.CanManage(context), "An enabled staff-admin claim and approved IP pass the network gate");
    context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
    Check(ipAccess.CanManage(context), "IPv4-mapped IPv6 connections match an IPv4 allowlist entry");
    context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.77");
    context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
    Check(!ipAccess.CanManage(context), "A forged forwarding header cannot pass the real connection-IP check");
    context.Connection.RemoteIpAddress = IPAddress.Loopback;
    context.User = Principal(user, false, "test");
    Check(!ipAccess.CanManage(context), "An approved IP does not make an ordinary user an administrator");
    context.User = Principal("admin@whitemeadow.com", true, "test");
    Check(!ipAccess.CanManage(context), "A foreign-domain administrator cannot use the admin gate");
    context.User = Principal(admin, true, "test");
    File.Delete(allowlist);
    Check(!ipAccess.CanManage(context), "Removing an allowlist takes effect immediately");

    var now = DateTimeOffset.UtcNow;
    var overlap = new[]
    {
        new UsageSessionEntry(user, "127.0.0.1", now.AddMinutes(-20), now.AddMinutes(-5), now.AddMinutes(-5), "Sign out"),
        new UsageSessionEntry(user, "127.0.0.1", now.AddMinutes(-10), now, now, "Sign out")
    };
    Check(Math.Abs(UsageSessionAnalytics.OnlineMinutes(overlap, now.AddMinutes(-30), now, now) - 20) < 0.0001,
        "Concurrent sessions count online minutes once");
    var (springStart, springEnd) = UsageSessionAnalytics.DateRangeUtc(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29));
    var (autumnStart, autumnEnd) = UsageSessionAnalytics.DateRangeUtc(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25));
    Check((springEnd - springStart).TotalHours == 23 && (autumnEnd - autumnStart).TotalHours == 25,
        "UK calendar date filtering handles both daylight-saving transitions");

    var logsPath = Path.Combine(directory, "Data", "Logs", "site_usage.csv");
    Directory.CreateDirectory(Path.GetDirectoryName(logsPath)!);
    var started = now.AddMinutes(-60);
    await File.WriteAllTextAsync(logsPath,
        "SessionId,Username,IpAddress,SessionStartedUtc,LastSeenUtc,SessionEndedUtc,EndReason,ActivityResumed\n" +
        $"resume-cookie,{user},127.0.0.1,{started:O},{now.AddMinutes(-50):O},{now.AddMinutes(-45):O},Inactivity timeout (estimated),false\n");
    var log = new UsageSessionLog(NullLogger<UsageSessionLog>.Instance, environment);
    var principal = Principal(user, false, "resume-cookie");
    await log.TouchAsync(principal, "127.0.0.1");
    var intervals = await log.SnapshotAsync();
    Check(intervals.Count == 2 && intervals.Count(entry => entry.IsResumed) == 1, "Resuming activity preserves the old interval and starts a new one");
    var stats = UsageSessionAnalytics.SummariseUsers(intervals, DateTimeOffset.UtcNow)[user];
    Check(stats.LoginsLast30Days == 1 && stats.LastLoginUtc == started, "Resuming activity does not inflate sign-ins or change the last login");
    await log.EndAsync(principal);
    var reloadedLog = new UsageSessionLog(NullLogger<UsageSessionLog>.Instance, environment);
    var persisted = await reloadedLog.SnapshotAsync();
    Check(persisted.Count == 2 && persisted.Single(entry => entry.IsResumed).EndReason == "Sign out",
        "Resumed intervals and actual sign-out survive a process restart");
    await reloadedLog.TouchAsync(principal, "127.0.0.1");
    Check((await reloadedLog.SnapshotAsync()).Count == 2, "A signed-out usage session is not revived by a stale heartbeat");

    Console.WriteLine($"PASS: {checks} account, domain, IP-gate and usage-security checks.");
}
finally { Directory.Delete(directory, true); }

void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
}

async Task Reject<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { checks++; return; }
    throw new Exception("FAIL: " + message);
}

static ClaimsPrincipal Principal(string email, bool admin, string session)
{
    var claims = new List<Claim> { new(ClaimTypes.Name, email), new(UsageSessionLog.SessionClaim, session) };
    if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
    return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
}

sealed class TestEnvironment(string directory) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "SparesSecurityTests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = directory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = Path.Combine(directory, "wwwroot");
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
