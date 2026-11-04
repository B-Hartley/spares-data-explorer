namespace ClickSpares.SparesExplorer.Services;

/// <summary>Reloads server-folder exports after their file sizes/timestamps settle.</summary>
public sealed class DataRefreshWorker(ImportService imports, ILogger<DataRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? observed = null;
        string? loaded = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var fingerprint = imports.SourceFingerprint();
                if (fingerprint == observed && fingerprint != loaded)
                {
                    await imports.ReloadAsync(stoppingToken);
                    loaded = fingerprint;
                }
                observed = fingerprint;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "CSV refresh failed; retaining the last valid dataset"); }
        }
    }
}
