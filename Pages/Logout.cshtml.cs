using ClickSpares.SparesExplorer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClickSpares.SparesExplorer.Pages;

public sealed class LogoutModel(UsageSessionLog usageLog, ILogger<LogoutModel> logger) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        try { await usageLog.EndAsync(User); }
        catch (Exception ex) { logger.LogError(ex, "Unable to record logout usage"); }
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
