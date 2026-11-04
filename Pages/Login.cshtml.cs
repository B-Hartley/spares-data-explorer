using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ClickSpares.SparesExplorer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ClickSpares.SparesExplorer.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(UserStore users, UsageSessionLog usageLog, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet(string? returnUrl)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Index");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (!ModelState.IsValid) return Page();
        AuthenticatedUser? managed;
        try { managed = await users.AuthenticateAsync(Input.Email, Input.Password); }
        catch (Exception error)
        {
            logger.LogError(error, "The account database could not be read at sign-in");
            ModelState.AddModelError(string.Empty, "Sign-in is temporarily unavailable. Contact the administrator.");
            return Page();
        }
        if (managed is null)
        {
            ModelState.AddModelError(string.Empty, "The email address or password is incorrect.");
            return Page();
        }
        var sessionId = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, managed.Email),
            new(UsageSessionLog.SessionClaim, sessionId),
            new("SparesExplorer.UserStamp", managed.SecurityStamp)
        };
        if (managed.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        try { await usageLog.StartAsync(sessionId, managed.Email, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"); }
        catch (Exception error) { logger.LogError(error, "Unable to record the sign-in usage interval"); }
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });
        return ReturnUrl is not null ? LocalRedirect(ReturnUrl) : RedirectToPage("/Index");
    }

    public sealed class LoginInput
    {
        [Required, EmailAddress, StringLength(254)]
        public string Email { get; set; } = string.Empty;

        [Required, StringLength(256), DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
