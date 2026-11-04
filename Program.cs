using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using ClickSpares.SparesExplorer.Models;
using ClickSpares.SparesExplorer.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

var creatingAdmin = args.Length > 0 && args[0] == "--create-admin";
var builder = WebApplication.CreateBuilder(creatingAdmin ? [] : args);

// This command runs locally and exits without starting any web listener.
if (creatingAdmin)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: dotnet run -- --create-admin name@clickspares.co.uk");
        Environment.ExitCode = 2;
        return;
    }
    try
    {
        if (!Console.IsInputRedirected) Console.Write("New administrator password (12+ characters): ");
        var password = ReadPassword();
        await new UserStore(builder.Environment).CreateFirstAdministratorAsync(args[1], password);
        Console.WriteLine("First administrator created. Configure Data/Auth/admin_allowed_ips.txt before using the administration pages.");
    }
    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    return;
}

const long MaximumUploadBytes = 100L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = MaximumUploadBytes;
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaximumUploadBytes);
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/", "Staff"));
builder.Services.AddHttpContextAccessor();
var keysPath = Path.Combine(builder.Environment.ContentRootPath, "Data", "Auth", "Keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection().SetApplicationName("ClickSpares.SparesExplorer")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// HTTP cookies are permitted only during Development and the middleware below
// rejects every non-loopback HTTP request before authentication can run.
var cookieSecurity = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "ClickSpares.SparesExplorer.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = cookieSecurity;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Error";
        options.Cookie.Name = "ClickSpares.SparesExplorer.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = cookieSecurity;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var email = context.Principal?.Identity?.Name ?? "";
            var stamp = context.Principal?.FindFirstValue("SparesExplorer.UserStamp");
            var valid = false;
            if (!string.IsNullOrEmpty(stamp) && AdminAccountScope.IsStaff(email))
            {
                try
                {
                    valid = await context.HttpContext.RequestServices.GetRequiredService<UserStore>()
                        .IsCurrentAsync(email, stamp, context.Principal!.IsInRole("Admin"));
                }
                catch (Exception error)
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILogger<UserStore>>()
                        .LogError(error, "Account validation failed; authentication was rejected");
                }
            }
            if (!valid)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            if (IsApi(context.HttpContext))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return context.Response.WriteAsJsonAsync(new { error = "Your session has ended. Please sign in again." });
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { error = "This account or connection does not have permission to access this page." });
        };
    });
builder.Services.AddAuthorization(options =>
{
    var staff = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
        .RequireAssertion(context => AdminAccountScope.IsStaff(context.User.Identity?.Name)).Build();
    options.AddPolicy("Staff", staff);
    options.FallbackPolicy = staff;
    options.AddPolicy("Admin", policy => policy.Combine(staff).RequireRole("Admin")
        .AddRequirements(new ApprovedAdminNetworkRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, ApprovedAdminNetworkHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many sign-in attempts. Please wait a minute and try again." }, cancellationToken);
    };
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<AdminIpAccess>();
builder.Services.AddSingleton<UsageSessionLog>();
builder.Services.AddHostedService<UsageLogExpiryWorker>();
builder.Services.AddSingleton<SparesStore>();
builder.Services.AddSingleton<ImportService>();
builder.Services.AddHostedService<DataRefreshWorker>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "same-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["X-Robots-Tag"] = "noindex, nofollow";
    headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (!context.Request.Path.StartsWithSegments("/css") && !context.Request.Path.StartsWithSegments("/js"))
        headers.CacheControl = "no-store";
    await next();
});
app.UseExceptionHandler("/Error");
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.Use(async (context, next) =>
{
    var ip = context.Connection.RemoteIpAddress;
    var loopback = ip is not null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    if (!context.Request.IsHttps && !(app.Environment.IsDevelopment() && loopback))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "HTTPS is required. Development HTTP access is restricted to loopback connections." });
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapRazorPages();

var api = app.MapGroup("/api").RequireAuthorization("Staff")
    .AddEndpointFilter<ApiAntiforgeryFilter>();
api.MapGet("/session", (HttpContext context, IAntiforgery antiforgery, AdminIpAccess access) => Results.Ok(new
{
    email = context.User.Identity!.Name,
    isAdmin = context.User.IsInRole("Admin"),
    canManage = access.CanManage(context),
    csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken
}));
api.MapPost("/session/heartbeat", async (HttpContext context, UsageSessionLog log) =>
{
    await log.TouchAsync(context.User, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    return Results.NoContent();
});
api.MapGet("/metadata", (SparesStore store) => Results.Ok(store.GetMetadata()));
api.MapPost("/explore", (ExploreRequest request, SparesStore store) =>
{
    try { return Results.Ok(store.Explore(request)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
api.MapPost("/requests", (ExploreRequest request, SparesStore store) =>
{
    try { return Results.Ok(store.GetRequests(request)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
api.MapPost("/reload", async (ImportService imports, SparesStore store, CancellationToken cancellationToken) =>
{
    try
    {
        await imports.ReloadAsync(cancellationToken);
        return Results.Ok(store.GetMetadata());
    }
    catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
}).RequireAuthorization("Admin");
app.MapImportEndpoints();

try { await app.Services.GetRequiredService<ImportService>().ReloadAsync(); }
catch (InvalidDataException error) { app.Logger.LogError(error, "Initial CSV import was rejected; no replacement data was activated"); }
app.Run();

static bool IsApi(HttpContext context) => context.Request.Path.StartsWithSegments("/api");

static string ReadPassword()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var password = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return password.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; continue; }
        if (!char.IsControl(key.KeyChar))
        {
            if (password.Length >= 256) throw new ArgumentException("The password must not exceed 256 characters.");
            password.Append(key.KeyChar);
        }
    }
}

public sealed class ApiAntiforgeryFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { error = "Your page security token has expired. Refresh the page and try again." });
            }
        }
        return await next(context);
    }
}

public partial class Program;
