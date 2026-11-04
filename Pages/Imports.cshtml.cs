using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClickSpares.SparesExplorer.Pages;

[Authorize(Policy = "Admin")]
public sealed class ImportsModel : PageModel
{
    public void OnGet() { }
}
