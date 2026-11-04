using System.Globalization;

namespace ClickSpares.SparesExplorer.Services;

/// <summary>
/// Read-only projection of an existing usage-log row. Session IDs are deliberately
/// excluded from the administration UI and usage calculations.
/// </summary>
public sealed record UsageSessionEntry(
    string Username,
    string IpAddress,
    DateTimeOffset StartedUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset? EndedUtc,
    string EndReason,
    bool IsResumed = false);

public sealed record UsageUserStatistics(
    DateTimeOffset? LastLoginUtc,
    int LoginsLast30Days,
    long MinutesLast30Days);

/// <summary>
/// The log has one session per sign-in and a one-minute heartbeat. An idle
/// session is estimated to end five minutes after its last heartbeat.
/// Overlapping sessions for the same user are merged for total online time.
/// All intervals use UTC; date selectors use Europe/London calendar days.
/// </summary>
public static class UsageSessionAnalytics
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    public static TimeZoneInfo UkTimeZone { get; } = ResolveUkTimeZone();

    private static TimeZoneInfo ResolveUkTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        }
    }

    public static DateTimeOffset InUk(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, UkTimeZone);

    public static string FormatUk(DateTimeOffset utc) =>
        InUk(utc).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    public static (DateTimeOffset Start, DateTimeOffset EndExclusive) DateRangeUtc(
        DateOnly from, DateOnly to)
    {
        if (to < from) throw new ArgumentException("The end date must not precede the start date.");
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, UkTimeZone), TimeSpan.Zero),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end, UkTimeZone), TimeSpan.Zero));
    }

    public static DateTimeOffset EffectiveEnd(UsageSessionEntry entry, DateTimeOffset now)
    {
        var ended = entry.EndedUtc ?? entry.LastSeenUtc.Add(IdleTimeout);
        if (ended > now) ended = now;
        return ended < entry.StartedUtc ? entry.StartedUtc : ended;
    }

    public static bool IsActive(UsageSessionEntry entry, DateTimeOffset now) =>
        entry.EndedUtc is null && entry.StartedUtc <= now &&
        entry.LastSeenUtc.Add(IdleTimeout) > now;

    public static string Status(UsageSessionEntry entry, DateTimeOffset now) =>
        IsActive(entry, now) ? "Active" :
        entry.EndedUtc is null ? "Inactivity timeout (estimated)" :
        string.IsNullOrWhiteSpace(entry.EndReason) ? "Ended" : entry.EndReason;

    public static bool Overlaps(UsageSessionEntry entry,
        DateTimeOffset start, DateTimeOffset endExclusive, DateTimeOffset now) =>
        entry.StartedUtc < endExclusive &&
        EffectiveEnd(entry, now) >= start &&
        entry.StartedUtc <= now;

    public static double SessionMinutes(UsageSessionEntry entry,
        DateTimeOffset start, DateTimeOffset endExclusive, DateTimeOffset now)
    {
        var from = entry.StartedUtc > start ? entry.StartedUtc : start;
        var to = EffectiveEnd(entry, now);
        if (to > endExclusive) to = endExclusive;
        return Math.Max(0, (to - from).TotalMinutes);
    }

    // For a single user, union session intervals rather than double-counting
    // simultaneous sign-ins in different browsers or devices.
    public static double OnlineMinutes(IEnumerable<UsageSessionEntry> entries,
        DateTimeOffset start, DateTimeOffset endExclusive, DateTimeOffset now)
    {
        var intervals = entries
            .Where(entry => entry.StartedUtc <= now)
            .Select(entry => (
                Start: entry.StartedUtc > start ? entry.StartedUtc : start,
                End: Min(EffectiveEnd(entry, now), endExclusive)))
            .Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ThenBy(interval => interval.End);

        DateTimeOffset? mergedStart = null;
        DateTimeOffset mergedEnd = default;
        var minutes = 0d;
        foreach (var interval in intervals)
        {
            if (mergedStart is null)
            {
                mergedStart = interval.Start;
                mergedEnd = interval.End;
            }
            else if (interval.Start <= mergedEnd)
            {
                if (interval.End > mergedEnd) mergedEnd = interval.End;
            }
            else
            {
                minutes += (mergedEnd - mergedStart.Value).TotalMinutes;
                mergedStart = interval.Start;
                mergedEnd = interval.End;
            }
        }
        if (mergedStart is not null)
            minutes += (mergedEnd - mergedStart.Value).TotalMinutes;
        return minutes;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    public static IReadOnlyDictionary<string, UsageUserStatistics> SummariseUsers(
        IReadOnlyList<UsageSessionEntry> entries, DateTimeOffset now)
    {
        var from = now.AddDays(-30);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Username))
            .GroupBy(entry => entry.Username.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var signIns = group.Where(entry => !entry.IsResumed).ToArray();
                    DateTimeOffset? lastLogin = signIns.Length == 0 ? null : signIns.Max(entry => entry.StartedUtc);
                    var loginCount = signIns.Count(entry =>
                        entry.StartedUtc >= from && entry.StartedUtc <= now);
                    var minutes = OnlineMinutes(group, from, now, now);
                    return new UsageUserStatistics(
                        lastLogin,
                        loginCount,
                        (long)Math.Round(minutes, 0, MidpointRounding.AwayFromZero));
                },
                StringComparer.OrdinalIgnoreCase);
    }
}
