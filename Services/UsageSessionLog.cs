using System.Globalization;
using System.Security.Claims;
using System.Text;

namespace ClickSpares.SparesExplorer.Services;

/// <summary>Server-local usage intervals; resumed activity does not count as another sign-in.</summary>
public sealed class UsageSessionLog(ILogger<UsageSessionLog> logger, IWebHostEnvironment environment)
{
    public const string SessionClaim = "SparesExplorer.SessionId";
    private const string IdleReason = "Inactivity timeout (estimated)";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WriteInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SessionRow> sessions = new(StringComparer.Ordinal);
    private readonly string path = Path.Combine(environment.ContentRootPath, "Data", "Logs", "site_usage.csv");
    private bool loaded;

    private sealed record SessionRow(string Id, string User, string Ip,
        DateTimeOffset Started, DateTimeOffset Seen, DateTimeOffset? Ended, string Reason, bool Resumed = false);

    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private async Task LoadAsync()
    {
        if (loaded) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            foreach (var row in DelimitedText.Parse(await File.ReadAllTextAsync(path)).Skip(1))
            {
                if (row.Count < 7 ||
                    !DateTimeOffset.TryParse(row[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started) ||
                    !DateTimeOffset.TryParse(row[4], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen)) continue;
                DateTimeOffset? ended = DateTimeOffset.TryParse(row[5], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var end) ? end : null;
                var resumed = row.Count > 7 && bool.TryParse(row[7], out var flag) && flag;
                sessions[row[0]] = new SessionRow(row[0], row[1], row[2], started, seen, ended, row[6], resumed);
            }
        }
        loaded = true;
    }

    private bool CloseIdle(DateTimeOffset now)
    {
        var changed = false;
        foreach (var row in sessions.Values.Where(row => row.Ended is null && now - row.Seen >= IdleTimeout).ToArray())
        {
            sessions[row.Id] = row with { Ended = row.Seen.Add(IdleTimeout), Reason = IdleReason };
            changed = true;
        }
        return changed;
    }

    private async Task SaveAsync()
    {
        var csv = new StringBuilder("SessionId,Username,IpAddress,SessionStartedUtc,LastSeenUtc,SessionEndedUtc,EndReason,ActivityResumed\r\n");
        foreach (var row in sessions.Values.OrderBy(row => row.Started))
        {
            csv.Append(string.Join(",", Csv(row.Id), Csv(row.User), Csv(row.Ip),
                Timestamp(row.Started), Timestamp(row.Seen),
                row.Ended is null ? "" : Timestamp(row.Ended.Value), Csv(row.Reason), row.Resumed ? "true" : "false"))
                .Append("\r\n");
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, csv.ToString(), new UTF8Encoding(true));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task StartAsync(string id, string user, string ip)
    {
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            CloseIdle(now);
            sessions[id] = new SessionRow(id, user, ip, now, now, null, "");
            await SaveAsync();
        }
        finally { gate.Release(); }
    }

    public async Task TouchAsync(ClaimsPrincipal principal, string ip = "unknown")
    {
        var id = principal.FindFirstValue(SessionClaim);
        var user = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(id) || !AdminAccountScope.IsStaff(user)) return;
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var expired = CloseIdle(now);
            if (!sessions.TryGetValue(id, out var row))
            {
                // A valid authentication cookie can outlive a rotated usage log.
                sessions[id] = new SessionRow(id, user!, ip, now, now, null, "", true);
            }
            else
            {
                if (!row.User.Equals(user, StringComparison.OrdinalIgnoreCase)) return;
                if (row.Ended is not null)
                {
                    if (row.Reason != IdleReason) { if (expired) await SaveAsync(); return; }
                    // Archive the old interval and keep the cookie's stable key
                    // attached to the new interval, including after a restart.
                    var archiveId = id + "." + Guid.NewGuid().ToString("N");
                    sessions[archiveId] = row with { Id = archiveId };
                    sessions[id] = new SessionRow(id, user!, ip, now, now, null, "", true);
                }
                else
                {
                    if (now - row.Seen < WriteInterval) { if (expired) await SaveAsync(); return; }
                    sessions[id] = row with { Seen = now };
                }
            }
            await SaveAsync();
        }
        finally { gate.Release(); }
    }

    public async Task EndAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(SessionClaim);
        if (string.IsNullOrWhiteSpace(id)) return;
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var expired = CloseIdle(now);
            if (!sessions.TryGetValue(id, out var row) || row.Ended is not null)
            {
                if (expired) await SaveAsync();
                return;
            }
            sessions[id] = row with { Seen = now, Ended = now, Reason = "Sign out" };
            await SaveAsync();
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<UsageSessionEntry>> SnapshotAsync()
    {
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            return sessions.Values.Select(row => new UsageSessionEntry(
                row.User, row.Ip, row.Started, row.Seen, row.Ended, row.Reason, row.Resumed)).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task ExpireIdleAsync()
    {
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            if (CloseIdle(DateTimeOffset.UtcNow)) await SaveAsync();
        }
        catch (Exception error) { logger.LogError(error, "Usage log expiry failed"); }
        finally { gate.Release(); }
    }
}

public sealed class UsageLogExpiryWorker(UsageSessionLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await log.ExpireIdleAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await log.ExpireIdleAsync();
    }
}
