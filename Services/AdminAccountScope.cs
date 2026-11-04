namespace ClickSpares.SparesExplorer.Services;

/// <summary>Staff access is deliberately restricted to one exact email domain.</summary>
public static class AdminAccountScope
{
    public const string StaffDomain = "clickspares.co.uk";

    public static bool IsStaff(string? email)
    {
        var value = UserStore.NormaliseEmail(email ?? "");
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') &&
            value[(at + 1)..].Equals(StaffDomain, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSiteAdministrator(string? email) => IsStaff(email);
    public static bool HasAccess(string? email) => IsStaff(email);
    public static bool CanManage(string? actor, string? target) => IsStaff(actor) && IsStaff(target);
    public static string ScopeDescription(string? email) => IsStaff(email)
        ? "Click Spares staff accounts" : "No account-management access";
}
