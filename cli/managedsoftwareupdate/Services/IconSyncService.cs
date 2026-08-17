using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core;
using Cimian.Core.Services;
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Mirrors the repo's icons directory down to the local icons directory so the
/// Managed Software Center GUI has real tiles instead of generated solid-color
/// fallbacks. Icons resolve as icon_name from the catalog when set, otherwise
/// "&lt;name&gt;.png". Uses conditional requests (If-Modified-Since) so steady-state
/// runs cost one 304 per icon; a missing repo icon (404) is silently skipped and
/// the GUI keeps its generated fallback.
/// </summary>
public class IconSyncService
{
    private const int MaxParallelDownloads = 4;

    private readonly HttpClient _httpClient;
    private readonly CimianConfig _config;

    public IconSyncService(CimianConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? CimianHttpClientFactory.CreateHttpClient(config, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Syncs the icons for the given catalog items. Never throws — icon sync is
    /// cosmetic and must not affect the run outcome.
    /// </summary>
    public async Task SyncAsync(IEnumerable<(string Name, CatalogItem? Catalog)> items, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(CimianPaths.IconsDir);

            var iconFiles = items
                .Select(i => ResolveIconFileName(i.Name, i.Catalog))
                .Where(f => f != null)
                .Select(f => f!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (iconFiles.Count == 0)
                return;

            int downloaded = 0, unchanged = 0, missing = 0, failed = 0;

            await Parallel.ForEachAsync(
                iconFiles,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = cancellationToken },
                async (iconFile, ct) =>
                {
                    switch (await SyncOneAsync(iconFile, ct))
                    {
                        case SyncResult.Downloaded: Interlocked.Increment(ref downloaded); break;
                        case SyncResult.Unchanged: Interlocked.Increment(ref unchanged); break;
                        case SyncResult.Missing: Interlocked.Increment(ref missing); break;
                        case SyncResult.Failed: Interlocked.Increment(ref failed); break;
                    }
                });

            if (downloaded > 0 || failed > 0)
                ConsoleLogger.Info($"Icon sync: {downloaded} downloaded, {unchanged} up-to-date, {missing} not in repo, {failed} failed");
            else
                ConsoleLogger.Detail($"    Icon sync: {unchanged} up-to-date, {missing} not in repo");
        }
        catch (OperationCanceledException)
        {
            // Run cancelled — partial icon sync is fine, the next run finishes it.
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Icon sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the icon filename for an item: catalog icon_name when set,
    /// otherwise "&lt;name&gt;.png". Rejects names that would escape the icons
    /// directory.
    /// </summary>
    internal static string? ResolveIconFileName(string name, CatalogItem? catalog)
    {
        var fileName = !string.IsNullOrWhiteSpace(catalog?.IconName)
            ? catalog!.IconName!.Trim()
            : (string.IsNullOrWhiteSpace(name) ? null : name.Trim() + ".png");

        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        // Icons live flat in the icons directory — reject separators and traversal.
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
            return null;

        return fileName;
    }

    private enum SyncResult { Downloaded, Unchanged, Missing, Failed }

    private async Task<SyncResult> SyncOneAsync(string iconFile, CancellationToken cancellationToken)
    {
        var localPath = Path.Combine(CimianPaths.IconsDir, iconFile);
        var url = $"{_config.SoftwareRepoURL.TrimEnd('/')}/icons/{Uri.EscapeDataString(iconFile)}";

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (File.Exists(localPath))
            {
                request.Headers.IfModifiedSince = File.GetLastWriteTimeUtc(localPath);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return SyncResult.Unchanged;

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return SyncResult.Missing;

            if (!response.IsSuccessStatusCode)
            {
                ConsoleLogger.Detail($"    Icon fetch failed ({(int)response.StatusCode}): {iconFile}");
                return SyncResult.Failed;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return SyncResult.Missing;

            await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);

            // Stamp the server's Last-Modified so the next If-Modified-Since matches.
            var lastModified = response.Content.Headers.LastModified;
            if (lastModified.HasValue)
            {
                File.SetLastWriteTimeUtc(localPath, lastModified.Value.UtcDateTime);
            }

            ConsoleLogger.Detail($"    Icon downloaded: {iconFile}");
            return SyncResult.Downloaded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Detail($"    Icon fetch failed: {iconFile} ({ex.Message})");
            return SyncResult.Failed;
        }
    }
}
