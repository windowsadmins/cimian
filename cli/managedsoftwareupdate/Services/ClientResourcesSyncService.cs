using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Downloads <c>client_resources/{name}.zip</c> from the software repo (Munki-style)
/// and extracts MSC branding assets into <see cref="CimianPaths.ManagedInstallsRoot"/>.
/// Cosmetic only — failures are logged and never fail the run.
/// </summary>
public class ClientResourcesSyncService
{
    private static readonly string SyncStatePath = Path.Combine(
        CimianPaths.ClientResourcesDir, ".repo-sync.json");

    private readonly HttpClient _httpClient;
    private readonly CimianConfig _config;

    public ClientResourcesSyncService(CimianConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? CimianHttpClientFactory.CreateHttpClient(config, TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// Sync client resources from the repo. Never throws.
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(CimianPaths.BrandingDir);
            Directory.CreateDirectory(CimianPaths.ClientResourcesDir);

            var candidates = BuildZipNameCandidates(_config);
            if (candidates.Count == 0)
                return;

            foreach (var zipName in candidates)
            {
                var url = BuildZipUrl(zipName);
                var result = await TrySyncZipAsync(url, zipName, cancellationToken);
                switch (result)
                {
                    case ZipSyncResult.Applied:
                        ConsoleLogger.Info($"Client resources synced from {zipName}");
                        return;
                    case ZipSyncResult.Unchanged:
                        ConsoleLogger.Detail($"    Client resources up-to-date ({zipName})");
                        return;
                    case ZipSyncResult.NotFound:
                        ConsoleLogger.Detail($"    Client resources zip not in repo: {zipName}");
                        continue;
                    case ZipSyncResult.Failed:
                        ConsoleLogger.Warn($"Client resources sync failed for {zipName}");
                        return;
                }
            }

            ConsoleLogger.Detail("    No client_resources zip found in repo for any candidate name");

            // Munki deletes its custom.zip when the repo no longer has one; the equivalent
            // here is taking back the files the last zip put down.
            var stale = await LoadStateAsync();
            if (stale?.ExtractedFiles is { Count: > 0 })
            {
                var removed = RemoveExtractedFiles(stale.ExtractedFiles, keep: null);
                ConsoleLogger.Info($"Client resources removed from repo; cleared {removed} file(s)");
            }
            if (stale != null)
                File.Delete(SyncStatePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Client resources sync failed: {ex.Message}");
        }
    }

    private enum ZipSyncResult { Applied, Unchanged, NotFound, Failed }

    private string BuildZipUrl(string zipBaseName) => BuildZipUrl(_config, zipBaseName);

    /// <summary>
    /// <c>ClientResourceURL</c> when set, otherwise <c>SoftwareRepoURL/client_resources</c>,
    /// as Munki builds it.
    /// </summary>
    internal static string BuildZipUrl(CimianConfig config, string zipBaseName)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.ClientResourceURL)
            ? $"{config.SoftwareRepoURL.TrimEnd('/')}/client_resources"
            : config.ClientResourceURL.Trim().TrimEnd('/');
        return $"{baseUrl}/{Uri.EscapeDataString(zipBaseName)}.zip";
    }

    /// <summary>
    /// Candidate zip base names (without .zip), first match wins.
    /// Mirrors manifest identity precedence where practical.
    /// </summary>
    internal static List<string> BuildZipNameCandidates(CimianConfig config)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            var trimmed = name.Trim();
            if (seen.Add(trimmed))
                names.Add(trimmed);
        }

        // Munki tries ClientResourcesFilename before the manifest name.
        var fileName = config.ClientResourcesFilename?.Trim();
        if (!string.IsNullOrEmpty(fileName) &&
            fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".zip".Length];
        Add(fileName);

        if (config.UseClientCertificate && config.UseClientCertificateCNAsClientIdentifier)
        {
            var cn = CimianHttpClientFactory.GetClientCertificateCN(config);
            Add(cn);
        }

        Add(config.ClientIdentifier);
        Add(Environment.MachineName);
        Add("site_default");

        return names;
    }

    private async Task<ZipSyncResult> TrySyncZipAsync(
        string url,
        string zipName,
        CancellationToken cancellationToken)
    {
        try
        {
            var priorState = await LoadStateAsync();
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (priorState != null &&
                string.Equals(priorState.SourceUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(priorState.ETag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", priorState.ETag);
                else if (priorState.LastModifiedUtc.HasValue)
                    request.Headers.IfModifiedSince = priorState.LastModifiedUtc.Value;
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return ZipSyncResult.NotFound;

            if (response.StatusCode == HttpStatusCode.NotModified)
                return ZipSyncResult.Unchanged;

            if (!response.IsSuccessStatusCode)
            {
                ConsoleLogger.Detail($"    Client resources fetch failed ({(int)response.StatusCode}): {url}");
                return ZipSyncResult.Failed;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var extracted = await ExtractZipAsync(stream, cancellationToken);

            // A file the previous zip had and this one does not would otherwise stay
            // on screen for ever, e.g. a banner the admin took out.
            if (priorState?.ExtractedFiles is { Count: > 0 })
            {
                var keep = new HashSet<string>(extracted.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                RemoveExtractedFiles(priorState.ExtractedFiles, keep);
            }

            var etag = response.Headers.ETag?.Tag;
            DateTimeOffset? lastMod = response.Content.Headers.LastModified;
            await SaveStateAsync(new SyncState
            {
                SourceUrl = url,
                ZipName = zipName,
                ETag = etag,
                LastModifiedUtc = lastMod?.UtcDateTime,
                ExtractedUtc = DateTime.UtcNow,
                ExtractedFiles = extracted,
            });

            return ZipSyncResult.Applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Detail($"    Client resources fetch error: {ex.Message}");
            return ZipSyncResult.Failed;
        }
    }

    private static async Task<List<ExtractedFile>> ExtractZipAsync(Stream zipStream, CancellationToken cancellationToken)
    {
        var extracted = new List<ExtractedFile>();
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name))
                continue; // directory entry

            var target = ClientResourcesPathMapper.MapEntryToLocalPath(entry.FullName);
            if (target == null)
                continue;

            var fullTarget = Path.GetFullPath(target);
            if (!ClientResourcesPathMapper.IsUnderManagedInstalls(fullTarget))
                throw new InvalidDataException($"Zip entry escapes ManagedInstalls: {entry.FullName}");

            var parent = Path.GetDirectoryName(fullTarget);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            await using var entryStream = entry.Open();
            await using var fileStream = new FileStream(
                fullTarget,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            await entryStream.CopyToAsync(fileStream, cancellationToken);
            await fileStream.DisposeAsync();
            extracted.Add(new ExtractedFile
            {
                Path = fullTarget,
                WrittenUtc = File.GetLastWriteTimeUtc(fullTarget),
            });
        }

        return extracted;
    }

    /// <summary>
    /// Deletes files an earlier zip extracted, except those in <paramref name="keep"/>.
    /// A file changed since it was extracted belongs to someone else now and is left alone.
    /// </summary>
    internal static int RemoveExtractedFiles(IEnumerable<ExtractedFile> files, ISet<string>? keep)
    {
        var removed = 0;
        foreach (var file in files)
        {
            try
            {
                if (string.IsNullOrEmpty(file.Path) || keep?.Contains(file.Path) == true)
                    continue;
                var full = Path.GetFullPath(file.Path);
                if (!ClientResourcesPathMapper.IsUnderManagedInstalls(full) || !File.Exists(full))
                    continue;
                if (File.GetLastWriteTimeUtc(full) != file.WrittenUtc)
                    continue;
                File.Delete(full);
                removed++;
            }
            catch (Exception ex)
            {
                ConsoleLogger.Detail($"    Could not remove old client resource {file.Path}: {ex.Message}");
            }
        }
        return removed;
    }

    private static async Task<SyncState?> LoadStateAsync()
    {
        try
        {
            if (!File.Exists(SyncStatePath))
                return null;
            var json = await File.ReadAllTextAsync(SyncStatePath);
            return JsonSerializer.Deserialize<SyncState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static async Task SaveStateAsync(SyncState state)
    {
        Directory.CreateDirectory(CimianPaths.ClientResourcesDir);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(SyncStatePath, json);
    }

    private sealed class SyncState
    {
        public string? SourceUrl { get; set; }
        public string? ZipName { get; set; }
        public string? ETag { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
        public DateTime ExtractedUtc { get; set; }
        public List<ExtractedFile>? ExtractedFiles { get; set; }
    }

    internal sealed class ExtractedFile
    {
        public string Path { get; set; } = string.Empty;
        public DateTime WrittenUtc { get; set; }
    }
}

/// <summary>
/// Maps zip entry paths to local ManagedInstalls locations.
/// Supports Cimian-native layout and Munki <c>resources/</c> banner images.
/// </summary>
public static class ClientResourcesPathMapper
{
    private static readonly string ManagedRoot = Path.GetFullPath(CimianPaths.ManagedInstallsRoot);

    public static bool IsUnderManagedInstalls(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        return normalized.StartsWith(ManagedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, ManagedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the local file path for a zip entry, or null to skip (e.g. Munki HTML templates).
    /// </summary>
    public static string? MapEntryToLocalPath(string entryFullName)
    {
        if (string.IsNullOrWhiteSpace(entryFullName))
            return null;

        var normalized = entryFullName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is "." or ".."))
            return null;

        // Munki HTML templates are not used by WinUI MSC.
        if (normalized.StartsWith("templates/", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(normalized, "preferences.yaml", StringComparison.OrdinalIgnoreCase))
            return CimianPaths.PreferencesYaml;

        // branding.yaml at the zip root is where an admin most often puts it.
        if (string.Equals(normalized, "branding.yaml", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(CimianPaths.ClientResourcesDir, "branding.yaml");

        if (normalized.StartsWith("branding/", StringComparison.OrdinalIgnoreCase))
        {
            var leaf = normalized["branding/".Length..];
            if (!IsSafeLeafName(leaf))
                return null;
            return Path.Combine(CimianPaths.BrandingDir, leaf);
        }

        if (normalized.StartsWith("client_resources/", StringComparison.OrdinalIgnoreCase))
        {
            var leaf = normalized["client_resources/".Length..];
            if (!IsSafeLeafName(leaf))
                return null;
            return Path.Combine(CimianPaths.ClientResourcesDir, leaf);
        }

        // Munki compatibility: resources/branding*.png -> branding/
        if (normalized.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
        {
            var leaf = normalized["resources/".Length..];
            if (!IsSafeLeafName(leaf))
                return null;

            var fileName = Path.GetFileName(leaf);
            if (IsBrandingImage(fileName))
                return Path.Combine(CimianPaths.BrandingDir, fileName);

            return Path.Combine(CimianPaths.ClientResourcesDir, fileName);
        }

        return null;
    }

    private static bool IsBrandingImage(string fileName)
    {
        if (!fileName.StartsWith("branding", StringComparison.OrdinalIgnoreCase))
            return false;
        return fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeLeafName(string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf))
            return false;
        if (leaf.Contains(':') || leaf.Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ')))
            return false;
        if (leaf.StartsWith('/') || leaf.StartsWith('\\'))
            return false;
        return true;
    }
}
