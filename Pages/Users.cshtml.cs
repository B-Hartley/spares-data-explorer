using System.ComponentModel.DataAnnotations;
using ClickSpares.SparesExplorer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClickSpares.SparesExplorer.Pages;

[Authorize(Policy = "Admin")]
public sealed class UsersModel(UserStore userStore, UsageSessionLog usageLog, AdminIpAccess ipAccess, ILogger<UsersModel> logger) : PageModel
{
    public IReadOnlyList<UserSummary> Accounts { get; private set; } = [];
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    private IReadOnlyDictionary<string, UsageUserStatistics> usage =
        new Dictionary<string, UsageUserStatistics>(StringComparer.OrdinalIgnoreCase);
    public string ScopeDescription => AdminAccountScope.ScopeDescription(User.Identity?.Name);
    public string LastLogin(string email) =>
        usage.TryGetValue(email, out var summary) && summary.LastLoginUtc is { } last
            ? UsageSessionAnalytics.FormatUk(last) : "Never";
    public long MinutesLast30Days(string email) =>
        usage.TryGetValue(email, out var summary) ? summary.MinutesLast30Days : 0;
    public int LoginsLast30Days(string email) =>
        usage.TryGetValue(email, out var summary) ? summary.LoginsLast30Days : 0;
    private string Actor => User.Identity?.Name ?? "";
    private bool IsAllowed => ipAccess.CanManage(HttpContext) && AdminAccountScope.HasAccess(Actor);

    private async Task LoadPageDataAsync()
    {
        Accounts = await userStore.ListForAdminAsync(Actor);
        var records = await usageLog.SnapshotAsync();
        usage = UsageSessionAnalytics.SummariseUsers(
            records.Where(record => AdminAccountScope.CanManage(Actor, record.Username)).ToArray(),
            DateTimeOffset.UtcNow);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!IsAllowed) return StatusCode(403);
        await LoadPageDataAsync();
        return Page();
    }

    private async Task<IActionResult> ResultAsync(string? message = null, string? error = null)
    {
        Message = message;
        Error = error;
        await LoadPageDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(string email, string password, bool isAdmin = false)
    {
        if (!IsAllowed) return StatusCode(403);
        try
        {
            await userStore.CreateForAdminAsync(Actor, email, password, isAdmin);
            return await ResultAsync($"Account created for {UserStore.NormaliseEmail(email)}.");
        }
        catch (ArgumentException ex) { return await ResultAsync(error: ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to create account");
            return await ResultAsync(error: "Unable to save the account. Check the server logs.");
        }
    }

    public async Task<IActionResult> OnPostPasswordAsync(string email, string newPassword)
    {
        if (!IsAllowed) return StatusCode(403);
        try
        {
            await userStore.ResetPasswordForAdminAsync(Actor, email, newPassword);
            return await ResultAsync($"Password reset for {UserStore.NormaliseEmail(email)}. Existing sessions are invalidated.");
        }
        catch (ArgumentException ex) { return await ResultAsync(error: ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to reset password");
            return await ResultAsync(error: "Unable to save the password. Check the server logs.");
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(string email, bool confirmDelete = false)
    {
        if (!IsAllowed) return StatusCode(403);
        if (!confirmDelete) return await ResultAsync(error: "Confirm the account deletion before continuing.");
        var actingAdmin = User.Identity?.Name ?? "";
        try
        {
            await userStore.DeleteAsync(email, actingAdmin);
            logger.LogInformation("Managed account {Email} deleted by {Admin}",
                UserStore.NormaliseEmail(email), UserStore.NormaliseEmail(actingAdmin));
            return await ResultAsync($"Account deleted for {UserStore.NormaliseEmail(email)}. Existing sessions are revoked.");
        }
        catch (ArgumentException ex) { return await ResultAsync(error: ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to delete managed account");
            return await ResultAsync(error: "Unable to delete the account. Check the server logs.");
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(string email, bool enabled)
    {
        if (!IsAllowed) return StatusCode(403);
        if (string.Equals(email, User.Identity?.Name, StringComparison.OrdinalIgnoreCase) && !enabled)
            return await ResultAsync(error: "You cannot disable your own account.");
        try
        {
            await userStore.SetEnabledForAdminAsync(Actor, email, enabled);
            return await ResultAsync($"{UserStore.NormaliseEmail(email)} {(enabled ? "enabled" : "disabled")}.");
        }
        catch (ArgumentException ex) { return await ResultAsync(error: ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to change account state");
            return await ResultAsync(error: "Unable to update the account. Check the server logs.");
        }
    }
}
