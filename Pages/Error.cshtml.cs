using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClickSpares.SparesExplorer.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel : PageModel
{
    public IActionResult OnGet() => Render();
    public IActionResult OnPost() => Render();

    private IActionResult Render()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        var originalPath = HttpContext.Features.Get<IExceptionHandlerPathFeature>()?.Path;
        if (originalPath?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true)
            return new JsonResult(new { error = "The request could not be completed. Please try again or contact the administrator." })
                { StatusCode = StatusCodes.Status500InternalServerError };
        return Page();
    }
}
