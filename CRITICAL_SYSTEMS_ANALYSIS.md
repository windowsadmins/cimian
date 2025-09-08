# Cimian Critical Systems Technical Analysis

This document provides deep technical analysis of the most complex and critical systems in Cimian that require careful migration from Go to C#. These systems contain the most refined logic and present the highest risk during migration.

## Table of Contents
1. [Conditional Items Evaluation Engine](#conditional-items-evaluation-engine)
2. [Multi-Format Installer Management](#multi-format-installer-management)
3. [Download Management with Retry Logic](#download-management-with-retry-logic)
4. [Package Metadata Processing](#package-metadata-processing)
5. [Manifest Processing Pipeline](#manifest-processing-pipeline)
6. [Session-Based Logging System](#session-based-logging-system)

## Conditional Items Evaluation Engine

### Overview
The conditional items evaluation engine is the most sophisticated component in Cimian, implementing NSPredicate-style conditional evaluation with support for complex expressions, nested logic, and system fact collection.

### Go Implementation Analysis

#### Core Components (pkg/predicates/predicates.go)

**1. Expression Parser**
```go
// Supports complex expressions like:
// "hostname CONTAINS 'DEV-' OR hostname CONTAINS 'TEST-' AND arch == 'x64'"
// "(domain == 'CORP' OR domain == 'EDU') AND NOT hostname CONTAINS 'Kiosk'"
```

**Key Features**:
- Recursive descent parsing
- Operator precedence handling
- Parentheses grouping support
- String literal parsing with quotes
- Special operators (ANY, NOT, OR, AND)

**2. System Facts Collection**
```go
type SystemFacts map[string]interface{}

// Collects facts like:
// - hostname, arch, domain, machine_type
// - os_version, os_vers_major, battery_state
// - enrolled_usage, enrolled_area (custom facts)
// - catalogs (array of available catalogs)
```

**3. Evaluation Engine**
```go
func (fc *FactsCollector) EvaluateConditionalItem(item *ConditionalItem) (bool, error)
func (fc *FactsCollector) compareValues(factValue interface{}, operator string, conditionValue interface{}) (bool, error)
```

**Key Logic**:
- Version-aware comparison for OS versions
- Array operations for multi-value facts
- Type coercion and string conversion
- Error handling for malformed expressions

### C# Migration Strategy

#### 1. Expression Parser Implementation

**Target Structure**:
```csharp
public interface IExpressionParser
{
    ParsedExpression Parse(string expression);
    Task<bool> EvaluateAsync(ParsedExpression expression, SystemFacts facts);
}

public class ConditionalExpressionParser : IExpressionParser
{
    private readonly ILogger<ConditionalExpressionParser> _logger;
    
    // Recursive descent parser methods
    private Expression ParseOrExpression();
    private Expression ParseAndExpression(); 
    private Expression ParseNotExpression();
    private Expression ParseComparisonExpression();
    private Expression ParsePrimaryExpression();
}

public abstract class Expression
{
    public abstract Task<bool> EvaluateAsync(SystemFacts facts);
}

public class BinaryExpression : Expression
{
    public Expression Left { get; set; }
    public BinaryOperator Operator { get; set; }
    public Expression Right { get; set; }
}

public class ComparisonExpression : Expression
{
    public string FactKey { get; set; }
    public ComparisonOperator Operator { get; set; }
    public object Value { get; set; }
}
```

**Operator Enumeration**:
```csharp
public enum ComparisonOperator
{
    Equals,
    NotEquals,
    Contains,
    DoesNotContain,
    BeginsWith,
    EndsWith,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    In,
    Like
}

public enum BinaryOperator
{
    And,
    Or
}
```

#### 2. System Facts Collection

**Implementation**:
```csharp
public interface ISystemFactsCollector
{
    Task<SystemFacts> CollectAsync();
    Task<SystemFacts> CollectWithCacheAsync(TimeSpan cacheTimeout);
}

public class SystemFactsCollector : ISystemFactsCollector
{
    private readonly ILogger<SystemFactsCollector> _logger;
    private readonly IMemoryCache _cache;
    
    public async Task<SystemFacts> CollectAsync()
    {
        var facts = new SystemFacts();
        
        // Basic system facts
        facts["hostname"] = Environment.MachineName;
        facts["arch"] = GetArchitecture();
        facts["domain"] = GetDomainName();
        facts["machine_type"] = await GetMachineTypeAsync();
        
        // OS version facts
        var osVersion = Environment.OSVersion;
        facts["os_version"] = osVersion.VersionString;
        facts["os_vers_major"] = osVersion.Version.Major;
        facts["os_build_number"] = osVersion.Version.Build;
        
        // Enhanced facts
        facts["enrolled_usage"] = await GetEnrollmentUsageAsync();
        facts["enrolled_area"] = await GetEnrollmentAreaAsync();
        facts["joined_type"] = await GetJoinTypeAsync();
        facts["battery_state"] = GetBatteryState();
        
        return facts;
    }
    
    private string GetArchitecture()
    {
        return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        // TODO: Add ARM64 detection logic
    }
    
    private async Task<string> GetJoinTypeAsync()
    {
        // Use WMI or Windows APIs to determine:
        // "domain", "hybrid", "entra", "workgroup"
    }
}
```

#### 3. Version-Aware Comparison

**Critical Logic Preservation**:
```csharp
public class VersionAwareComparer : IValueComparer
{
    public bool Compare(object factValue, ComparisonOperator op, object conditionValue)
    {
        // Handle version strings specially
        if (IsVersionString(factValue) && IsVersionString(conditionValue))
        {
            return CompareVersions(factValue.ToString(), op, conditionValue.ToString());
        }
        
        // Handle numeric comparisons
        if (IsNumeric(factValue) && IsNumeric(conditionValue))
        {
            return CompareNumeric(factValue, op, conditionValue);
        }
        
        // Handle string comparisons
        return CompareStrings(factValue?.ToString(), op, conditionValue?.ToString());
    }
    
    private bool CompareVersions(string factVersion, ComparisonOperator op, string conditionVersion)
    {
        // Parse versions (10.0.19041, 11.0.22000, etc.)
        if (Version.TryParse(factVersion, out var fact) && 
            Version.TryParse(conditionVersion, out var condition))
        {
            return op switch
            {
                ComparisonOperator.GreaterThan => fact > condition,
                ComparisonOperator.GreaterThanOrEqual => fact >= condition,
                ComparisonOperator.LessThan => fact < condition,
                ComparisonOperator.LessThanOrEqual => fact <= condition,
                ComparisonOperator.Equals => fact == condition,
                ComparisonOperator.NotEquals => fact != condition,
                _ => throw new ArgumentException($"Operator {op} not supported for version comparison")
            };
        }
        
        // Fallback to string comparison
        return CompareStrings(factVersion, op, conditionVersion);
    }
}
```

### Migration Risks and Mitigation

**High-Risk Areas**:
1. **Expression Parsing**: Complex grammar with operator precedence
2. **Nested Evaluation**: Recursive conditional item processing
3. **System Facts**: Windows API integration for fact collection
4. **Performance**: Large manifest files with many conditions

**Mitigation Strategies**:
1. **Extensive Testing**: Test suite with complex expression samples
2. **Parser Generator**: Consider using ANTLR for robust parsing
3. **Incremental Migration**: Start with simple expressions, add complexity
4. **Performance Profiling**: Benchmark against Go implementation

---

## Multi-Format Installer Management

### Overview
Cimian supports multiple installer formats with sophisticated command-line argument processing, timeout management, and error handling.

### Go Implementation Analysis

#### Core Installer Types (pkg/installer/installer.go)

**1. MSI Installer Processing**
```go
func runMSIInstaller(item catalog.Item, localFile string, cfg *config.Configuration) (string, error) {
    args := []string{"/i", localFile, "/qn"}
    
    // Add custom MSI properties from pkginfo
    for key, val := range item.Installer.MSIProperties {
        args = append(args, fmt.Sprintf("%s=%s", key, val))
    }
    
    // Execute with timeout
    return runCMDWithTimeout("msiexec.exe", args, cfg.InstallerTimeoutMinutes)
}
```

**2. EXE Installer with Smart Flag Processing**
```go
func runEXEInstaller(item catalog.Item, localFile string, cfg *config.Configuration) (string, error) {
    args := []string{}
    
    // Handle verb (e.g., "install", "update")
    if item.Installer.Verb != "" {
        args = append(args, item.Installer.Verb)
    }
    
    // Handle switches (e.g., /silent, /S)
    for _, sw := range item.Installer.Switches {
        if strings.Contains(sw, "=") {
            parts := strings.SplitN(sw, "=", 2)
            args = append(args, fmt.Sprintf("/%s=%s", parts[0], quoteIfNeeded(parts[1])))
        } else {
            args = append(args, fmt.Sprintf("/%s", sw))
        }
    }
    
    // Smart flag processing with installer-aware defaults
    for _, flag := range item.Installer.Flags {
        // Complex logic for different flag formats
        // Supports: --flag, -flag, /flag, flag=value, etc.
    }
}
```

**3. Dynamic Timeout Calculation**
```go
func runCMDWithTimeout(command string, arguments []string, timeoutMinutes int) (string, error) {
    // Calculate timeout based on file size:
    // Base: 1 minute + 1 minute per 50MB
    // 100MB = ~3min, 500MB = ~11min, 1GB = ~21min
    if contentLength := getFileSize(arguments[1]); contentLength > 0 {
        calculatedTimeout := time.Minute + time.Duration(size/(50*1024*1024))*time.Minute
        if calculatedTimeout > timeout {
            timeout = calculatedTimeout
        }
    }
}
```

**4. PowerShell Script Execution**
```go
func runPowerShellScript(scriptPath string, args []string) error {
    psArgs := []string{
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", scriptPath,
    }
    psArgs = append(psArgs, args...)
    
    return runCMDWithTimeout("powershell.exe", psArgs, defaultTimeout)
}
```

### C# Migration Strategy

#### 1. Installer Factory Pattern

**Target Structure**:
```csharp
public interface IInstaller
{
    Task<InstallationResult> InstallAsync(CatalogItem item, string localFilePath, CancellationToken cancellationToken);
    Task<InstallationResult> UninstallAsync(CatalogItem item, CancellationToken cancellationToken);
    bool CanHandle(InstallerType type);
}

public class InstallerFactory : IInstallerFactory
{
    private readonly IEnumerable<IInstaller> _installers;
    
    public IInstaller GetInstaller(InstallerType type)
    {
        return _installers.FirstOrDefault(i => i.CanHandle(type)) 
            ?? throw new NotSupportedException($"Installer type {type} not supported");
    }
}
```

#### 2. MSI Installer Implementation

```csharp
public class MsiInstaller : IInstaller
{
    private readonly IProcessManager _processManager;
    private readonly ILogger<MsiInstaller> _logger;
    
    public async Task<InstallationResult> InstallAsync(CatalogItem item, string localFilePath, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "/i", $"\"{localFilePath}\"", "/qn" };
        
        // Add custom MSI properties
        if (item.Installer.MSIProperties != null)
        {
            foreach (var property in item.Installer.MSIProperties)
            {
                arguments.Add($"{property.Key}={property.Value}");
            }
        }
        
        // Add logging
        var logPath = Path.Combine(Path.GetTempPath(), $"msi_install_{Guid.NewGuid()}.log");
        arguments.AddRange(new[] { "/l*v", $"\"{logPath}\"" });
        
        var timeout = CalculateTimeout(localFilePath, item.Installer.TimeoutMinutes);
        
        var result = await _processManager.RunAsync(
            "msiexec.exe", 
            arguments, 
            timeout, 
            cancellationToken);
            
        return new InstallationResult
        {
            Success = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            Output = result.Output,
            LogPath = logPath
        };
    }
}
```

#### 3. EXE Installer with Smart Argument Processing

```csharp
public class ExeInstaller : IInstaller
{
    public async Task<InstallationResult> InstallAsync(CatalogItem item, string localFilePath, CancellationToken cancellationToken)
    {
        var arguments = BuildArgumentList(item, localFilePath);
        var timeout = CalculateTimeout(localFilePath, item.Installer.TimeoutMinutes);
        
        var result = await _processManager.RunAsync(
            localFilePath,
            arguments,
            timeout,
            cancellationToken);
            
        return ProcessResult(result, item);
    }
    
    private List<string> BuildArgumentList(CatalogItem item, string installerPath)
    {
        var args = new List<string>();
        
        // Handle verb
        if (!string.IsNullOrEmpty(item.Installer.Verb))
        {
            args.Add(item.Installer.Verb);
        }
        
        // Handle switches (/silent, /S, etc.)
        foreach (var sw in item.Installer.Switches ?? Enumerable.Empty<string>())
        {
            args.Add(ProcessSwitch(sw));
        }
        
        // Handle flags with smart formatting
        foreach (var flag in item.Installer.Flags ?? Enumerable.Empty<string>())
        {
            args.AddRange(ProcessFlag(flag, installerPath));
        }
        
        return args;
    }
    
    private string ProcessSwitch(string sw)
    {
        var trimmed = sw.Trim();
        
        if (trimmed.Contains("="))
        {
            var parts = trimmed.Split('=', 2);
            return $"/{parts[0]}={QuoteIfNeeded(parts[1])}";
        }
        
        return $"/{trimmed}";
    }
    
    private IEnumerable<string> ProcessFlag(string flag, string installerPath)
    {
        var trimmed = flag.Trim();
        
        // Preserve existing format if user provided dashes
        if (trimmed.StartsWith("--") || trimmed.StartsWith("-"))
        {
            if (trimmed.Contains("="))
            {
                yield return trimmed;
            }
            else if (trimmed.Contains(" "))
            {
                var parts = trimmed.Split(' ', 2);
                yield return parts[0];
                yield return QuoteIfNeeded(parts[1].Trim());
            }
            else
            {
                yield return trimmed;
            }
            yield break;
        }
        
        // Smart installer detection
        var detectedFormat = DetectInstallerFormat(installerPath);
        var flagPrefix = GetFlagPrefix(detectedFormat);
        
        if (trimmed.Contains("="))
        {
            var parts = trimmed.Split('=', 2);
            yield return $"{flagPrefix}{parts[0]}={QuoteIfNeeded(parts[1])}";
        }
        else
        {
            yield return $"{flagPrefix}{trimmed}";
        }
    }
}
```

#### 4. Process Manager with Elevation

**Critical Windows Integration**:
```csharp
public interface IProcessManager
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
    Task<ProcessResult> RunElevatedAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public class WindowsProcessManager : IProcessManager
{
    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Use PowerShell wrapper for proper elevation inheritance (critical!)
        var psCommand = BuildPowerShellCommand(fileName, arguments);
        
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = psCommand,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        
        process.OutputDataReceived += (s, e) => outputBuilder.AppendLine(e.Data);
        process.ErrorDataReceived += (s, e) => errorBuilder.AppendLine(e.Data);
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        var completed = await process.WaitForExitAsync(timeout, cancellationToken);
        
        if (!completed)
        {
            process.Kill(true);
            throw new TimeoutException($"Process timed out after {timeout}");
        }
        
        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            Output = outputBuilder.ToString(),
            Error = errorBuilder.ToString()
        };
    }
    
    private string BuildPowerShellCommand(string fileName, IEnumerable<string> arguments)
    {
        // Critical: This mirrors the Go implementation's elevation handling
        var escapedFileName = fileName.Replace("'", "''");
        var escapedArgs = arguments.Select(arg => $"'{arg.Replace("'", "''")}'");
        
        return $@"-NoProfile -Command ""
            $process = Start-Process -FilePath '{escapedFileName}' -ArgumentList {string.Join(",", escapedArgs)} -Wait -PassThru -NoNewWindow;
            exit $process.ExitCode
        """;
    }
}
```

### Migration Risks and Mitigation

**High-Risk Areas**:
1. **Elevation Handling**: Critical for Windows installer execution
2. **Argument Processing**: Complex flag format detection and conversion
3. **Timeout Logic**: Dynamic timeout calculation based on file size
4. **Error Handling**: Exit code interpretation and retry logic

**Mitigation Strategies**:
1. **Process Testing**: Extensive testing with real installers
2. **Elevation Validation**: Test with various privilege scenarios
3. **Argument Validation**: Test flag processing with multiple installer types
4. **Error Classification**: Map all possible exit codes and errors

---

## Download Management with Retry Logic

### Overview
Cimian's download system includes sophisticated retry logic, concurrent downloads, hash validation, and cloud storage integration.

### Go Implementation Analysis

#### Core Download Logic (pkg/download/download.go)

**1. Retry Logic with Exponential Backoff**
```go
// pkg/retry/retry.go
func Retry(config RetryConfig, action func() error) error {
    interval := config.InitialInterval
    
    for attempt := 1; attempt <= config.MaxRetries; attempt++ {
        err := action()
        if err == nil {
            return nil
        }
        
        // Check for non-retryable errors (404, etc.)
        var nonRetryableErr NonRetryableError
        if errors.As(err, &nonRetryableErr) {
            return err
        }
        
        // Exponential backoff with jitter
        time.Sleep(interval)
        interval = time.Duration(float64(interval) * config.Multiplier)
    }
}
```

**2. Concurrent Downloads with Semaphore**
```go
func InstallPendingUpdates(downloadItems map[string]string, cfg *config.Configuration, verbosity int, reporter utils.Reporter) (map[string]string, error) {
    resultPaths := make(map[string]string)
    var downloadErrors []error
    
    // Process downloads concurrently but limit concurrent operations
    semaphore := make(chan struct{}, cfg.MaxConcurrentDownloads)
    
    for name, url := range downloadItems {
        go func(name, url string) {
            semaphore <- struct{}{} // Acquire
            defer func() { <-semaphore }() // Release
            
            if err := DownloadFile(url, "", cfg, verbosity, reporter); err != nil {
                downloadErrors = append(downloadErrors, err)
                return
            }
            
            resultPaths[name] = getActualFilePathFromURL(url, cfg)
        }(name, url)
    }
}
```

**3. Hash Validation and Corruption Detection**
```go
func DownloadFile(url, unusedDest string, cfg *config.Configuration, verbosity int, reporter utils.Reporter, expectedHash ...string) error {
    // Check existing file hash
    if len(expectedHash) > 0 && expectedHash[0] != "" {
        if Verify(dest, expectedHash[0]) {
            return nil // File already exists and is valid
        } else {
            // Remove corrupt file
            os.Remove(dest)
        }
    }
    
    // Download with atomic operations
    tempDest := dest + ".downloading"
    defer os.Remove(tempDest)
    
    // Download logic with retry
    configRetry := retry.RetryConfig{
        MaxRetries:      3,
        InitialInterval: time.Second,
        Multiplier:      2.0,
    }
    
    return retry.Retry(configRetry, func() error {
        return performDownload(url, tempDest, cfg)
    })
}
```

**4. Dynamic Timeout Calculation**
```go
// Calculate timeout based on file size
if contentLength := resp.Header.Get("Content-Length"); contentLength != "" {
    if size := parseContentLength(contentLength); size > 0 {
        // 1 minute base + 1 minute per 50MB
        calculatedTimeout := time.Minute + time.Duration(size/(50*1024*1024))*time.Minute
        if calculatedTimeout > timeout {
            timeout = calculatedTimeout
        }
    }
}
```

### C# Migration Strategy

#### 1. Download Service with Polly Integration

**Target Structure**:
```csharp
public interface IDownloadService
{
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, DownloadResult>> DownloadBatchAsync(Dictionary<string, string> downloads, int maxConcurrent = 3, CancellationToken cancellationToken = default);
}

public class DownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly IAsyncPolicy _retryPolicy;
    private readonly ILogger<DownloadService> _logger;
    private readonly SemaphoreSlim _concurrencySemaphore;
    
    public DownloadService(HttpClient httpClient, IConfiguration config, ILogger<DownloadService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        var maxConcurrent = config.GetValue<int>("Download:MaxConcurrent", 3);
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        
        // Configure retry policy with Polly
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning("Download attempt {RetryCount} failed: {Error}. Retrying in {Delay}s", 
                        retryCount, outcome.Exception?.Message, timespan.TotalSeconds);
                });
    }
}
```

#### 2. Hash Validation and Atomic Operations

```csharp
public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress = null, CancellationToken cancellationToken = default)
{
    var destinationPath = GetDestinationPath(request.Url, request.Configuration);
    
    // Check existing file with hash validation
    if (!string.IsNullOrEmpty(request.ExpectedHash) && File.Exists(destinationPath))
    {
        var existingHash = await CalculateHashAsync(destinationPath);
        if (string.Equals(existingHash, request.ExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("File already exists with correct hash, skipping download: {Path}", destinationPath);
            return DownloadResult.Success(destinationPath);
        }
        
        _logger.LogWarning("Existing file has incorrect hash, removing and re-downloading: {Path}", destinationPath);
        File.Delete(destinationPath);
    }
    
    // Atomic download using temporary file
    var tempPath = destinationPath + ".downloading";
    
    try
    {
        var result = await _retryPolicy.ExecuteAsync(async () =>
        {
            await PerformDownloadAsync(request.Url, tempPath, progress, cancellationToken);
        });
        
        // Validate downloaded file
        if (!string.IsNullOrEmpty(request.ExpectedHash))
        {
            var downloadedHash = await CalculateHashAsync(tempPath);
            if (!string.Equals(downloadedHash, request.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Downloaded file hash mismatch. Expected: {request.ExpectedHash}, Actual: {downloadedHash}");
            }
        }
        
        // Atomic move to final destination
        File.Move(tempPath, destinationPath);
        
        return DownloadResult.Success(destinationPath);
    }
    finally
    {
        // Cleanup temp file
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }
}
```

#### 3. Concurrent Downloads with Progress Reporting

```csharp
public async Task<Dictionary<string, DownloadResult>> DownloadBatchAsync(
    Dictionary<string, string> downloads, 
    int maxConcurrent = 3, 
    CancellationToken cancellationToken = default)
{
    var results = new ConcurrentDictionary<string, DownloadResult>();
    var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    
    var tasks = downloads.Select(async kvp =>
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var request = new DownloadRequest
            {
                Url = kvp.Value,
                Configuration = _configuration
            };
            
            var result = await DownloadAsync(request, cancellationToken: cancellationToken);
            results[kvp.Key] = result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download {Name} from {Url}", kvp.Key, kvp.Value);
            results[kvp.Key] = DownloadResult.Failure(ex.Message);
        }
        finally
        {
            semaphore.Release();
        }
    });
    
    await Task.WhenAll(tasks);
    
    return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}
```

#### 4. Dynamic Timeout and HTTP Client Configuration

```csharp
private async Task PerformDownloadAsync(string url, string destinationPath, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
{
    // Create request with authentication
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    await _authService.AddAuthenticationAsync(request);
    
    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    response.EnsureSuccessStatusCode();
    
    // Calculate dynamic timeout based on content length
    var contentLength = response.Content.Headers.ContentLength ?? 0;
    var timeout = CalculateDynamicTimeout(contentLength);
    
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);
    
    using var stream = await response.Content.ReadAsStreamAsync();
    using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920);
    
    await CopyWithProgressAsync(stream, fileStream, contentLength, progress, cts.Token);
}

private TimeSpan CalculateDynamicTimeout(long contentLength)
{
    if (contentLength <= 0)
        return TimeSpan.FromMinutes(10); // Default timeout
    
    // 1 minute base + 1 minute per 50MB (matching Go implementation)
    var additionalMinutes = contentLength / (50 * 1024 * 1024);
    return TimeSpan.FromMinutes(1 + additionalMinutes);
}

private async Task CopyWithProgressAsync(Stream source, Stream destination, long totalBytes, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
{
    var buffer = new byte[81920];
    long totalRead = 0;
    var stopwatch = Stopwatch.StartNew();
    
    while (true)
    {
        var bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
        if (bytesRead == 0) break;
        
        await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
        totalRead += bytesRead;
        
        // Report progress
        if (progress != null && totalBytes > 0)
        {
            var percentage = (double)totalRead / totalBytes * 100;
            var speed = totalRead / stopwatch.Elapsed.TotalSeconds;
            
            progress.Report(new DownloadProgress
            {
                BytesReceived = totalRead,
                TotalBytes = totalBytes,
                ProgressPercentage = percentage,
                BytesPerSecond = speed
            });
        }
    }
}
```

### Migration Risks and Mitigation

**High-Risk Areas**:
1. **Retry Logic**: Complex error classification and backoff strategies
2. **Concurrency**: Thread-safe operations and resource management
3. **Progress Reporting**: Real-time progress updates without performance impact
4. **Hash Validation**: Performance impact of large file hashing

**Mitigation Strategies**:
1. **Polly Integration**: Use proven retry library instead of custom implementation
2. **Async/Await**: Leverage C# async patterns for better resource utilization
3. **Streaming**: Use streaming for large files to minimize memory usage
4. **Performance Testing**: Benchmark against Go implementation

---

## Summary

This technical analysis covers the most critical and complex systems in Cimian that require careful migration attention. Each system contains sophisticated logic that has been refined through 1,200+ commits and real-world usage.

The key to successful migration is:

1. **Preserve Exact Logic**: Maintain the same algorithms and decision trees
2. **Leverage C# Strengths**: Use .NET libraries like Polly, built-in async patterns
3. **Maintain Performance**: Ensure no regression in download speeds or processing time
4. **Comprehensive Testing**: Test every edge case and error condition
5. **Incremental Validation**: Validate each component before proceeding to the next

The migration plan provides a systematic approach to converting these complex systems while maintaining the reliability and performance that make Cimian effective for enterprise software deployment.
