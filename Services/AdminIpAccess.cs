using System.Net;
using Microsoft.AspNetCore.Authorization;

namespace ClickSpares.SparesExplorer.Services;

/// <summary>Administrative access fails closed unless the actual connection IP is approved.</summary>
public sealed class AdminIpAccess(IWebHostEnvironment environment, ILogger<AdminIpAccess> logger)
{
    private readonly string path = Path.Combine(environment.ContentRootPath, "Data", "Auth", "admin_allowed_ips.txt");

    public bool IsAllowed(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return false;
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        try
        {
            if (!File.Exists(path)) return false;
            foreach (var line in File.ReadLines(path))
            {
                var item = line.Split('#', 2)[0].Trim();
                if (IPAddress.TryParse(item, out var allowed) &&
                    (allowed.IsIPv4MappedToIPv6 ? allowed.MapToIPv4() : allowed).Equals(address))
                    return true;
            }
        }
        catch (IOException error) { logger.LogWarning(error, "The admin IP allowlist cannot be read"); }
        catch (UnauthorizedAccessException error) { logger.LogWarning(error, "The admin IP allowlist is inaccessible"); }
        return false;
    }

    public bool CanManage(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true &&
        context.User.IsInRole("Admin") &&
        AdminAccountScope.IsStaff(context.User.Identity.Name) && IsAllowed(context);
}

public sealed class ApprovedAdminNetworkRequirement : IAuthorizationRequirement;

public sealed class ApprovedAdminNetworkHandler(
    AdminIpAccess ipAccess,
    IHttpContextAccessor accessor) : AuthorizationHandler<ApprovedAdminNetworkRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ApprovedAdminNetworkRequirement requirement)
    {
        if (accessor.HttpContext is { } http && ipAccess.CanManage(http)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
