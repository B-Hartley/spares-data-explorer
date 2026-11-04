using System.Collections.Concurrent;
using ClickSpares.SparesExplorer.Models;
using Microsoft.AspNetCore.Antiforgery;

namespace ClickSpares.SparesExplorer.Services;

public sealed record ImportPreview(string Token, DateTimeOffset ExpiresAt, Metadata Metadata);
public sealed record ApplyImportRequest(string Token);

/// <summary>Validates a complete seven-table snapshot before changing the live dataset.</summary>
public sealed class ImportService
{
    public static readonly string[] RequiredFiles = ["CustomerRequests.csv", "custreqparts.csv", "products.csv", "parts.csv", "baseparts.csv", "manufacturers.csv", "factories.csv"];
    private sealed record PendingImport(string Owner, string Directory, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, PendingImport> pending = new();
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private readonly string importsPath;
    private readonly string previousPath;
    private readonly string stagingPath;
    private readonly SparesStore store;
    private readonly ILogger<ImportService> logger;

    public ImportService(IWebHostEnvironment environment, SparesStore store, ILogger<ImportService> logger)
    {
        this.store = store;
        this.logger = logger;
        var data = Path.Combine(environment.ContentRootPath, "Data");
        importsPath = Path.Combine(data, "Imports");
        previousPath = Path.Combine(data, "Previous");
        stagingPath = Path.Combine(data, "Staging");
        Directory.CreateDirectory(data);
        // Recover the last good snapshot if a process stopped during directory exchange.
        if (!Directory.Exists(importsPath) && Directory.Exists(previousPath))
            Directory.Move(previousPath, importsPath);
    }

    public async Task<ImportPreview> PreviewAsync(string owner, IFormFileCollection files, CancellationToken cancellationToken)
    {
        // Do not remove an expired staging directory while an already-started
        // apply is validating it or exchanging it with the live import folder.
        await mutationLock.WaitAsync(cancellationToken);
        try { CleanupExpired(); }
        finally { mutationLock.Release(); }
        if (files.Count != RequiredFiles.Length)
            throw new ArgumentException("Choose all seven CSV files for a complete snapshot. Nothing has been changed.");
        var selected = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in files)
        {
            var name = file.FileName.Replace('\\', '/').Split('/').Last();
            var canonical = RequiredFiles.FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (canonical is null || !selected.TryAdd(canonical, file))
                throw new ArgumentException("Only one copy of each of the seven named CSV files is allowed.");
            if (file.Length <= 0 || file.Length > 25 * 1024 * 1024)
                throw new ArgumentException($"{canonical} must be non-empty and no larger than 25 MB.");
            totalBytes += file.Length;
        }
        if (totalBytes > 95 * 1024 * 1024)
            throw new ArgumentException("The combined CSV files must be no larger than 95 MB.");
        var token = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(stagingPath, token);
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var (name, file) in selected)
            {
                await using var destination = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.CopyToAsync(destination, cancellationToken);
            }
            var snapshot = await Task.Run(() => SparesStore.LoadDirectory(directory), cancellationToken);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(20);
            pending[token] = new(owner, directory, expiresAt);
            return new(token, expiresAt, snapshot.Metadata);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    public async Task<Metadata> ApplyAsync(string owner, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 32)
            throw new ArgumentException("Preview the files before applying the import.");
        await mutationLock.WaitAsync(cancellationToken);
        try
        {
            if (!pending.TryGetValue(token, out var candidate) ||
                !string.Equals(candidate.Owner, owner, StringComparison.OrdinalIgnoreCase) ||
                candidate.ExpiresAt < DateTimeOffset.UtcNow)
                throw new ArgumentException("This preview has expired or belongs to another account. Validate the files again.");
            var snapshot = SparesStore.LoadDirectory(candidate.Directory);
            var movedPrevious = false;
            var installedNew = false;
            try
            {
                if (Directory.Exists(previousPath)) Directory.Delete(previousPath, true);
                if (Directory.Exists(importsPath))
                {
                    Directory.Move(importsPath, previousPath);
                    movedPrevious = true;
                }
                Directory.Move(candidate.Directory, importsPath);
                installedNew = true;
                store.ReplaceSnapshot(snapshot);
                pending.TryRemove(token, out _);
                logger.LogInformation("A validated CSV snapshot was applied by an administrator; {RequestCount} requests", snapshot.Metadata.RequestCount);
                return snapshot.Metadata;
            }
            catch
            {
                if (installedNew && Directory.Exists(importsPath)) Directory.Move(importsPath, candidate.Directory);
                if (movedPrevious && Directory.Exists(previousPath)) Directory.Move(previousPath, importsPath);
                throw;
            }
        }
        finally { mutationLock.Release(); }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken);
        try { await store.ReloadAsync(cancellationToken); }
        finally { mutationLock.Release(); }
    }

    public string SourceFingerprint()
    {
        if (!Directory.Exists(importsPath)) return "empty";
        return string.Join('|', Directory.EnumerateFiles(importsPath).Where(x => x.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(path => { var file = new FileInfo(path); return $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"; }));
    }

    private void CleanupExpired()
    {
        foreach (var pair in pending)
            if (pair.Value.ExpiresAt < DateTimeOffset.UtcNow && pending.TryRemove(pair.Key, out var expired))
                TryDelete(expired.Directory);
    }

    private void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { logger.LogWarning("An unused import staging directory could not be removed"); }
        catch (UnauthorizedAccessException) { logger.LogWarning("An unused import staging directory could not be removed"); }
    }
}

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/imports").RequireAuthorization("Admin");
        group.MapGet("/status", (SparesStore store) => Results.Ok(store.GetMetadata()));
        group.MapPost("/preview", async (HttpContext context, ImportService imports, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "Choose the seven CSV files." });
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                return Results.Ok(await imports.PreviewAsync(context.User.Identity!.Name!, form.Files, context.RequestAborted));
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Your security token has expired. Refresh the page and try again." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (IOException) { return Results.BadRequest(new { error = "The import files could not be read. Check server access and retry." }); }
            catch (UnauthorizedAccessException) { return Results.Problem("The server does not have permission to stage imports."); }
        }).DisableAntiforgery(); // Explicit validation above also supports multipart form requests.
        group.MapPost("/apply", async (ApplyImportRequest request, HttpContext context, ImportService imports, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                return Results.Ok(await imports.ApplyAsync(context.User.Identity!.Name!, request.Token, context.RequestAborted));
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Your security token has expired. Refresh the page and try again." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (IOException) { return Results.BadRequest(new { error = "The snapshot could not be applied. The previous dataset remains available." }); }
            catch (UnauthorizedAccessException) { return Results.Problem("The server does not have permission to apply imports."); }
        });
    }
}
