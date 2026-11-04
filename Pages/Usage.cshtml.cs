using System.Globalization;
using ClickSpares.SparesExplorer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClickSpares.SparesExplorer.Pages;

public sealed record UsageHistoryRow(
    UsageSessionEntry Session,
    DateTimeOffset EffectiveEndUtc,
    string Status,
    double MinutesWithinRange);

[Authorize(Policy = "Admin")]
public sealed class UsageModel(
    UsageSessionLog usageLog,
    AdminIpAccess ipAccess,
    ILogger<UsageModel> logger) : PageModel
{
    private const int PageSize = 50;
    public DateOnly FromDate { get; private set; }
    public DateOnly ToDate { get; private set; }
    public string SelectedUser { get; private set; } = "";
    public IReadOnlyList<string> AvailableUsers { get; private set; } = [];
    public IReadOnlyList<UsageHistoryRow> Rows { get; private set; } = [];
    public string? Error { get; private set; }
    public int SessionsFound { get; private set; }
    public int DistinctUsers { get; private set; }
    public long OnlineMinutes { get; private set; }
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public string FromValue => FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string ToValue => ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string FormatTimestamp(DateTimeOffset value) => UsageSessionAnalytics.FormatUk(value);

    public async Task<IActionResult> OnGetAsync(string? from, string? to, string? user, int pageNumber = 1)
    {
        // The log includes usernames and connection IPs. Apply both existing
        // administration gates BEFORE reading any usage history.
        var actingAdmin = User.Identity?.Name ?? "";
        if (!ipAccess.CanManage(HttpContext) || !AdminAccountScope.HasAccess(actingAdmin))
            return StatusCode(403);

        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(UsageSessionAnalytics.InUk(now).DateTime);
        ToDate = today;
        FromDate = today.AddDays(-29);
        SelectedUser = user?.Trim() ?? "";

        if (to is not null)
        {
            if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedTo) ||
                parsedTo < new DateOnly(2000, 1, 30) ||
                parsedTo > today.AddDays(1))
            {
                Error = "Enter a valid end date (no later than tomorrow).";
                return Page();
            }
            ToDate = parsedTo;
            FromDate = parsedTo.AddDays(-29);
        }
        if (from is not null)
        {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedFrom) ||
                parsedFrom < new DateOnly(2000, 1, 1))
            {
                Error = "Enter a valid start date.";
                return Page();
            }
            FromDate = parsedFrom;
        }
        if (ToDate < FromDate || ToDate.DayNumber - FromDate.DayNumber >= 366)
        {
            Error = "Choose a date range of no more than 366 days.";
            return Page();
        }

        if (SelectedUser.Length > 0 &&
            !AdminAccountScope.CanManage(actingAdmin, SelectedUser))
            return StatusCode(403);

        try
        {
            var all = (await usageLog.SnapshotAsync())
                .Where(entry => AdminAccountScope.CanManage(actingAdmin, entry.Username))
                .ToArray();
            AvailableUsers = all.Select(entry => entry.Username)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var (start, end) = UsageSessionAnalytics.DateRangeUtc(FromDate, ToDate);
            var selected = all
                .Where(entry => SelectedUser.Length == 0 ||
                    entry.Username.Equals(SelectedUser, StringComparison.OrdinalIgnoreCase))
                .Where(entry => UsageSessionAnalytics.Overlaps(entry, start, end, now))
                .OrderByDescending(entry => entry.StartedUtc)
                .ToArray();
            SessionsFound = selected.Length;
            DistinctUsers = selected.Select(entry => entry.Username)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            OnlineMinutes = (long)Math.Round(
                selected.GroupBy(entry => entry.Username, StringComparer.OrdinalIgnoreCase)
                    .Sum(group => UsageSessionAnalytics.OnlineMinutes(group, start, end, now)),
                0, MidpointRounding.AwayFromZero);
            TotalPages = Math.Max(1, (int)Math.Ceiling(SessionsFound / (double)PageSize));
            CurrentPage = Math.Clamp(pageNumber, 1, TotalPages);
            Rows = selected.Skip((CurrentPage - 1) * PageSize).Take(PageSize)
                .Select(entry => new UsageHistoryRow(
                    entry,
                    UsageSessionAnalytics.EffectiveEnd(entry, now),
                    UsageSessionAnalytics.Status(entry, now),
                    UsageSessionAnalytics.SessionMinutes(entry, start, end, now)))
                .ToArray();
        }
        catch (Exception error)
        {
            logger.LogError(error, "Unable to read admin usage history");
            Error = "Unable to read session history. Check the server logs.";
        }
        return Page();
    }
}
