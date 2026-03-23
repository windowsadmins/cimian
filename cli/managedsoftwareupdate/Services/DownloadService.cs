using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for downloading packages with hash verification
/// Features: HEAD request for size, resumable downloads, bandwidth monitoring
/// Migrated from Go pkg/download
/// </summary>
public class DownloadService
{
    private readonly HttpClient _httpClient;
    private readonly CimianConfig _config;
    
    // Download configuration constants
    private const int DefaultTimeoutMinutes = 10;
    private const int HeadRequestTimeoutSeconds = 30;
    private const long BytesPerMinuteForTimeout = 50 * 1024 * 1024; // 50MB/min minimum assumed speed
    private const int MinBandwidthBytesPerSec = 50 * 1024; // 50KB/s minimum before stall detection
    private const int StallCheckIntervalSeconds = 30;
    private const int MaxStallDurationSeconds = 120; // 2 minutes max stall before retry
    private const int BandwidthLogIntervalSeconds = 10;
    private const int MaxRetries = 5;
    private const int BufferSize = 64 * 1024; // 64KB buffer

    public DownloadService(CimianConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? CimianHttpClientFactory.CreateHttpClient(config, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Downloads a file from URL to local path with resume support and bandwidth monitoring
    /// </summary>
    public async Task<bool> DownloadFileAsync(
        string url,
        string localPath,
        string? expectedHash = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        ConsoleLogger.Detail($"    Starting download url: {url}");

        // Check if file exists and matches hash
        if (File.Exists(localPath) && !string.IsNullOrEmpty(expectedHash))
        {
            ConsoleLogger.Detail($"    Verifying cached file: {localPath}");
            var existingHash = CalculateSHA256(localPath);
            if (existingHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLogger.Info($"Using cached file: {Path.GetFileName(localPath)}");
                ConsoleLogger.Detail($"    Hash verification passed for cached file: {localPath}");
                return true;
            }
            ConsoleLogger.Detail($"    Cached file hash mismatch, re-downloading expected: {expectedHash.Substring(0, 12)}... got: {existingHash.Substring(0, 12)}...");
        }

        var tempPath = localPath + ".downloading";
        var fileName = Path.GetFileName(localPath);

        // Perform HEAD request to get file size and check resume support
        long totalBytes = -1;
        bool supportsResume = false;
        TimeSpan timeout = TimeSpan.FromMinutes(DefaultTimeoutMinutes);

        try
        {
            using var headCts = new CancellationTokenSource(TimeSpan.FromSeconds(HeadRequestTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, headCts.Token);
            
            var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _httpClient.SendAsync(headRequest, linkedCts.Token);
            
            if (headResponse.IsSuccessStatusCode)
            {
                totalBytes = headResponse.Content.Headers.ContentLength ?? -1;
                supportsResume = headResponse.Headers.AcceptRanges.Contains("bytes");
                
                // Calculate dynamic timeout based on file size
                if (totalBytes > 0)
                {
                    var calculatedMinutes = 2 + (totalBytes / BytesPerMinuteForTimeout);
                    if (calculatedMinutes > DefaultTimeoutMinutes)
                    {
                        timeout = TimeSpan.FromMinutes(calculatedMinutes);
                        ConsoleLogger.Detail($"    Large file detected size_mb: {totalBytes / (1024 * 1024)} calculated_timeout_minutes: {calculatedMinutes} supports_resume: {supportsResume}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Detail($"    HEAD request failed, proceeding with default timeout: {ex.Message}");
        }

        // Retry loop with resume support
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Check for existing partial download
                long startByte = 0;
                if (supportsResume && File.Exists(tempPath))
                {
                    var existingInfo = new FileInfo(tempPath);
                    if (existingInfo.Length > 0)
                    {
                        startByte = existingInfo.Length;
                        var percentComplete = totalBytes > 0 ? (double)startByte / totalBytes * 100 : 0;
                        ConsoleLogger.Info($"Resuming partial download file: {fileName} existing_bytes: {startByte} total_bytes: {totalBytes} percent_complete: {percentComplete:F1}%");
                    }
                }

                // Create request with Range header if resuming
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (startByte > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(startByte, null);
                }

                // Create timeout cancellation token
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                
                // Handle response codes
                if (startByte > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    ConsoleLogger.Warn($"Range not satisfiable, restarting download from beginning");
                    File.Delete(tempPath);
                    startByte = 0;
                    continue; // Retry from beginning
                }
                
                response.EnsureSuccessStatusCode();

                // Get expected size for this response
                var expectedSize = response.Content.Headers.ContentLength ?? (totalBytes > 0 ? totalBytes - startByte : -1);
                ConsoleLogger.Detail($"    Download started size: {expectedSize} bytes dest: {tempPath} resume_from: {startByte}");

                // Open file for append if resuming, create new otherwise
                var fileMode = startByte > 0 ? FileMode.Append : FileMode.Create;
                using (var fileStream = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, true))
                using (var httpStream = await response.Content.ReadAsStreamAsync(linkedCts.Token))
                {
                    var written = await CopyWithBandwidthMonitoringAsync(
                        httpStream, 
                        fileStream, 
                        expectedSize, 
                        startByte,
                        totalBytes,
                        fileName, 
                        progress, 
                        linkedCts.Token);
                    
                    // Validate total file size
                    var actualTotal = startByte + written;
                    if (totalBytes > 0 && actualTotal != totalBytes)
                    {
                        throw new InvalidOperationException($"Incomplete download: expected {totalBytes} bytes, got {actualTotal} bytes");
                    }
                }

                // Verify hash before finalizing
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    var downloadedHash = CalculateSHA256(tempPath);
                    if (!downloadedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleLogger.Warn($"Hash mismatch after download expected: {expectedHash.Substring(0, 12)}... got: {downloadedHash.Substring(0, 12)}...");
                        try { File.Delete(tempPath); } catch { /* ignore */ }
                        throw new InvalidOperationException($"Hash mismatch: expected {expectedHash}, got {downloadedHash}");
                    }
                }

                // Move temp file to final path
                File.Move(tempPath, localPath, overwrite: true);

                ConsoleLogger.Detail($"    File saved successfully file: {localPath}");
                return true;
            }
            catch (DownloadStalledException ex)
            {
                // Stall detected - partial file preserved for resume
                lastException = ex;
                ConsoleLogger.Warn($"Download stalled (attempt {attempt}/{MaxRetries}): {ex.Message}");
                if (attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    ConsoleLogger.Info($"Retrying in {delay.TotalSeconds}s with resume...");
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Don't retry if user cancelled
            }
            catch (Exception ex)
            {
                lastException = ex;
                ConsoleLogger.Warn($"Download attempt {attempt}/{MaxRetries} failed: {ex.Message}");
                if (attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    ConsoleLogger.Info($"Retrying in {delay.TotalSeconds}s...");
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        // All retries exhausted
        ConsoleLogger.Error($"Failed to download {url} after {MaxRetries} attempts: {lastException?.Message}");
        
        // Clean up temp file on final failure (unless it's a stall - keep for next run)
        if (lastException is not DownloadStalledException && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
        }
        
        return false;
    }

    /// <summary>
    /// Copies data with bandwidth monitoring and stall detection
    /// </summary>
    private async Task<long> CopyWithBandwidthMonitoringAsync(
        Stream source,
        Stream destination,
        long expectedSize,
        long startByte,
        long totalSize,
        string fileName,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long written = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastBandwidthLog = DateTime.UtcNow;
        var lastBandwidthBytes = 0L;
        var lastStallCheckBytes = 0L;
        var lastStallCheckTime = DateTime.UtcNow;
        var stallWarningIssued = false;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            written += bytesRead;

            var now = DateTime.UtcNow;

            // Bandwidth logging every 10 seconds
            if ((now - lastBandwidthLog).TotalSeconds >= BandwidthLogIntervalSeconds)
            {
                var bytesInPeriod = written - lastBandwidthBytes;
                var periodSeconds = (now - lastBandwidthLog).TotalSeconds;
                var currentSpeed = bytesInPeriod / periodSeconds;
                var overallSpeed = written / stopwatch.Elapsed.TotalSeconds;

                var etaStr = "";
                if (overallSpeed > 0 && totalSize > 0)
                {
                    var remaining = totalSize - (startByte + written);
                    var etaSeconds = remaining / overallSpeed;
                    etaStr = FormatDuration(TimeSpan.FromSeconds(etaSeconds));
                }

                var percentComplete = totalSize > 0 ? (double)(startByte + written) / totalSize * 100 : 0;

                ConsoleLogger.Detail($"    Download progress file: {fileName} downloaded_mb: {written / (1024 * 1024)} total_mb: {totalSize / (1024 * 1024)} percent: {percentComplete:F1}% current_speed: {FormatSpeed(currentSpeed)} avg_speed: {FormatSpeed(overallSpeed)} eta: {etaStr}");

                lastBandwidthLog = now;
                lastBandwidthBytes = written;
            }

            // Stall detection every 30 seconds
            if ((now - lastStallCheckTime).TotalSeconds >= StallCheckIntervalSeconds)
            {
                var bytesInPeriod = written - lastStallCheckBytes;
                var periodSeconds = (now - lastStallCheckTime).TotalSeconds;
                var currentSpeed = bytesInPeriod / periodSeconds;

                if (currentSpeed < MinBandwidthBytesPerSec)
                {
                    if (stallWarningIssued)
                    {
                        // Second consecutive stall - fail to trigger resume
                        ConsoleLogger.Warn($"Download stalled file: {fileName} stall_duration: {(now - lastStallCheckTime).TotalSeconds}s bytes_in_period: {bytesInPeriod} speed_bytes_sec: {currentSpeed:F0} downloaded_so_far: {written}");
                        throw new DownloadStalledException($"Download stalled (<{MinBandwidthBytesPerSec / 1024} KB/s for {MaxStallDurationSeconds}s), partial file preserved for resume");
                    }
                    else
                    {
                        // First stall warning
                        ConsoleLogger.Warn($"Download speed critically low, monitoring for stall file: {fileName} speed_bytes_sec: {currentSpeed:F0} threshold_bytes_sec: {MinBandwidthBytesPerSec}");
                        stallWarningIssued = true;
                    }
                }
                else
                {
                    // Reset stall warning if speed recovered
                    stallWarningIssued = false;
                }

                lastStallCheckBytes = written;
                lastStallCheckTime = now;
            }

            // Report progress
            if (progress != null && totalSize > 0)
            {
                var overallPercent = (double)(startByte + written) / totalSize * 100;
                progress.Report(overallPercent);
            }
        }

        // Log completion
        var elapsed = stopwatch.Elapsed;
        var avgSpeed = written / elapsed.TotalSeconds;
        ConsoleLogger.Info($"Download completed file: {fileName} size_mb: {written / (1024 * 1024)} duration: {elapsed:hh\\:mm\\:ss} avg_speed: {FormatSpeed(avgSpeed)}");

        return written;
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
            return $"{bytesPerSecond:F0} B/s";
        if (bytesPerSecond < 1024 * 1024)
            return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
            return $"{duration.Seconds}s";
        if (duration.TotalHours < 1)
            return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }

    /// <summary>
    /// Downloads a catalog item's installer
    /// </summary>
    public async Task<string?> DownloadItemAsync(
        CatalogItem item,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(item.Installer.Location))
        {
            // Script-only item
            return null;
        }

        var url = BuildFullUrl(item.Installer.Location);
        var localPath = GetCachePath(item);

        var success = await DownloadFileAsync(
            url,
            localPath,
            item.Installer.Hash,
            progress,
            cancellationToken);

        return success ? localPath : null;
    }

    /// <summary>
    /// Downloads multiple items
    /// </summary>
    public async Task<Dictionary<string, string>> DownloadItemsAsync(
        IEnumerable<CatalogItem> items,
        IProgress<(string ItemName, double Percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>();
        var itemList = items.ToList();
        var count = 0;

        foreach (var item in itemList)
        {
            count++;
            var itemProgress = new Progress<double>(p =>
            {
                progress?.Report((item.Name, p));
            });

            var path = await DownloadItemAsync(item, itemProgress, cancellationToken);
            if (!string.IsNullOrEmpty(path))
            {
                result[item.Name] = path;
            }

            ConsoleLogger.Info($"Downloaded {count}/{itemList.Count}: {item.Name}");
        }

        return result;
    }

    /// <summary>
    /// Builds full URL from location
    /// </summary>
    public string BuildFullUrl(string location)
    {
        if (location.StartsWith("http://") || location.StartsWith("https://"))
        {
            return location;
        }

        var normalizedLocation = location.Replace("\\", "/");
        if (!normalizedLocation.StartsWith("/"))
        {
            normalizedLocation = "/" + normalizedLocation;
        }

        return $"{_config.SoftwareRepoURL.TrimEnd('/')}/pkgs{normalizedLocation}";
    }

    /// <summary>
    /// Gets the local cache path for an item
    /// </summary>
    public string GetCachePath(CatalogItem item)
    {
        var fileName = Path.GetFileName(item.Installer.Location);
        
        // Organize by category if available
        if (!string.IsNullOrEmpty(item.Category))
        {
            var categoryPath = item.Category.Replace(" ", "_").ToLowerInvariant();
            return Path.Combine(_config.CachePath, categoryPath, fileName);
        }

        return Path.Combine(_config.CachePath, fileName);
    }

    /// <summary>
    /// Calculates SHA256 hash of a file
    /// </summary>
    public static string CalculateSHA256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Validates cache integrity and removes corrupt files
    /// </summary>
    public void ValidateAndCleanCache()
    {
        if (!Directory.Exists(_config.CachePath))
        {
            return;
        }

        var files = Directory.GetFiles(_config.CachePath, "*", SearchOption.AllDirectories);
        var corruptCount = 0;
        var abandonedDownloads = 0;

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            
            // Remove zero-byte files
            if (info.Length == 0)
            {
                try
                {
                    File.Delete(file);
                    corruptCount++;
                    ConsoleLogger.Info($"Removed corrupt file: {file}");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn($"Failed to remove corrupt file {file}: {ex.Message}");
                }
            }
            
            // Clean up old abandoned .downloading files (older than 24 hours)
            if (file.EndsWith(".downloading", StringComparison.OrdinalIgnoreCase))
            {
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24))
                {
                    try
                    {
                        File.Delete(file);
                        abandonedDownloads++;
                        ConsoleLogger.Info($"Removed abandoned download file: {file}");
                    }
                    catch (Exception ex)
                    {
                        ConsoleLogger.Warn($"Failed to remove abandoned download file {file}: {ex.Message}");
                    }
                }
                else
                {
                    ConsoleLogger.Detail($"Keeping recent partial download for resume: {file}");
                }
            }
        }

        if (corruptCount > 0 || abandonedDownloads > 0)
        {
            ConsoleLogger.Info($"Cache cleanup: removed {corruptCount} corrupt files, {abandonedDownloads} abandoned downloads");
        }
    }

    /// <summary>
    /// Gets cache statistics
    /// </summary>
    public (int FileCount, long TotalSize, int CorruptCount) GetCacheStatus()
    {
        if (!Directory.Exists(_config.CachePath))
        {
            return (0, 0, 0);
        }

        var files = Directory.GetFiles(_config.CachePath, "*", SearchOption.AllDirectories);
        var totalSize = 0L;
        var corruptCount = 0;

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            totalSize += info.Length;
            if (info.Length == 0)
            {
                corruptCount++;
            }
        }

        return (files.Length, totalSize, corruptCount);
    }

    /// <summary>
    /// Clears the cache selectively based on successful installations
    /// </summary>
    public void ClearCacheSelective(HashSet<string> successfullyInstalled)
    {
        if (!Directory.Exists(_config.CachePath))
        {
            return;
        }

        var files = Directory.GetFiles(_config.CachePath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            
            // Check if this file is for a successfully installed item
            var shouldRemove = successfullyInstalled.Any(item =>
                fileName.StartsWith(item, StringComparison.OrdinalIgnoreCase));

            if (shouldRemove)
            {
                try
                {
                    File.Delete(file);
                    ConsoleLogger.Info($"Removed cached file: {fileName}");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn($"Failed to remove cached file {fileName}: {ex.Message}");
                }
            }
        }
    }
}

/// <summary>
/// Exception thrown when a download stalls due to low bandwidth
/// The partial file is preserved to allow resume on retry
/// </summary>
public class DownloadStalledException : Exception
{
    public DownloadStalledException(string message) : base(message) { }
    public DownloadStalledException(string message, Exception innerException) : base(message, innerException) { }
}
