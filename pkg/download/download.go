// pkg/download/download.go - functions for downloading files from repo.

package download

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/retry"
	"github.com/windowsadmins/cimian/pkg/utils"
)

// NonRetryableError wraps errors that should not be retried
type NonRetryableError struct {
	Err error
}

func (e NonRetryableError) Error() string {
	return e.Err.Error()
}

func (e NonRetryableError) Unwrap() error {
	return e.Err
}

const (
	CacheExpirationDays = 30
	Timeout             = 10 * time.Minute  // Increased back to 10 minutes for large files
	LargeFileThreshold  = 100 * 1024 * 1024 // 100MB threshold for large files
)

// DownloadFile retrieves a file from `url` and saves it to the correct local path.
// If the URL indicates a catalog or manifest, it goes in the corresponding folder.
// Otherwise, it's treated as a package and goes to cfg.CachePath.
// verbosity and reporter are optional parameters for enhanced progress display
func DownloadFile(url, unusedDest string, cfg *config.Configuration, verbosity int, reporter utils.Reporter) error {
	if url == "" {
		return fmt.Errorf("DownloadFile: URL cannot be empty")
	}

	// If not set, use defaults
	if cfg.CatalogsPath == "" {
		cfg.CatalogsPath = `C:\ProgramData\ManagedInstalls\catalogs`
	}
	if cfg.CachePath == "" {
		cfg.CachePath = `C:\ProgramData\ManagedInstalls\Cache`
	}
	manifestsPath := `C:\ProgramData\ManagedInstalls\manifests` // if you want it configurable, move to cfg

	// 1) Figure out if this is a manifest, a catalog, or a package.
	lowerURL := strings.ToLower(url)
	isCatalog := strings.Contains(lowerURL, "/catalogs/")
	isManifest := strings.Contains(lowerURL, "/manifests/")
	isPackage := (!isCatalog && !isManifest)

	// 2) Possibly insert "/pkgs/" if it's a package but missing from the URL
	//    and the URL starts with your repo base.
	//    (You might want more robust checks if your repo has subfolders.)
	if isPackage && strings.HasPrefix(lowerURL, strings.ToLower(cfg.SoftwareRepoURL)) {
		if !strings.Contains(lowerURL, "/pkgs/") {
			// Insert /pkgs
			trimmedRepo := strings.TrimSuffix(cfg.SoftwareRepoURL, "/")
			url = trimmedRepo + "/pkgs" + strings.TrimPrefix(url, trimmedRepo)
			lowerURL = strings.ToLower(url)
			logging.Debug("Adjusted URL for package to include /pkgs/",
				"finalURL", url)
		}
	}

	// 3) Decide local base directory
	var baseDir string
	if isManifest {
		baseDir = manifestsPath
	} else if isCatalog {
		baseDir = cfg.CatalogsPath
	} else {
		// treat as package
		baseDir = cfg.CachePath
	}

	// 4) Determine local subPath
	//    If there's a known marker ("/catalogs/", "/manifests/", or "/pkgs/"),
	//    extract everything *after* that marker for local subfolder structure.
	var subPath string
	switch {
	case isManifest:
		// e.g. https://cimian.example.com/manifests/MyManifest.yaml
		parts := strings.SplitN(url, "/manifests/", 2)
		if len(parts) == 2 {
			subPath = parts[1]
		} else {
			// fallback => just the file name
			subPath = filepath.Base(url)
		}

	case isCatalog:
		parts := strings.SplitN(url, "/catalogs/", 2)
		if len(parts) == 2 {
			subPath = parts[1]
		} else {
			subPath = filepath.Base(url)
		}

	default:
		// for packages, we might see /pkgs/ or not
		if idx := strings.Index(lowerURL, "/pkgs/"); idx >= 0 {
			// e.g. "https://cimian.example.com/pkgs/apps/.../file.exe"
			subPath = url[idx+len("/pkgs/"):]
		} else {
			// fallback => just file name
			subPath = filepath.Base(url)
		}
	}

	// 5) Build final local path
	dest := filepath.Join(baseDir, subPath)
	dest = filepath.Clean(dest)

	// 6) Ensure parent directories exist
	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		return fmt.Errorf("failed to create directory structure for %s: %v", dest, err)
	}

	// 7) Start the retry logic - but only for network/download errors, not logical errors
	configRetry := retry.RetryConfig{
		MaxRetries:      3,
		InitialInterval: time.Second,
		Multiplier:      2.0,
	}

	// Check if file already exists and has content to avoid re-downloading
	if info, err := os.Stat(dest); err == nil && info.Size() > 0 {
		logging.Debug("File already exists with content, skipping download", "file", dest, "size", info.Size())
		return nil
	}

	// Use a temporary file during download to prevent corruption
	tempDest := dest + ".downloading"

	var downloadErr error
	downloadErr = retry.Retry(configRetry, func() error {
		logging.Debug("Starting download", "url", url, "destination", dest)

		// Remove any existing temp file from previous failed attempts
		os.Remove(tempDest)

		out, err := os.Create(tempDest)
		if err != nil {
			return fmt.Errorf("failed to create temporary download file: %v", err)
		}
		defer out.Close()

		req, err := utils.NewAuthenticatedRequest("GET", url, nil)
		if err != nil {
			return fmt.Errorf("failed to prepare HTTP request: %v", err)
		}

		// Configure HTTP client with better connection pooling and timeouts
		transport := &http.Transport{
			DisableKeepAlives:     false,
			MaxIdleConns:          10,
			IdleConnTimeout:       30 * time.Second,
			TLSHandshakeTimeout:   10 * time.Second,
			ExpectContinueTimeout: 1 * time.Second,
		}
		client := &http.Client{
			Timeout:   Timeout,
			Transport: transport,
		}
		resp, err := client.Do(req)
		if err != nil {
			return fmt.Errorf("failed to perform HTTP request: %v", err)
		}
		defer resp.Body.Close()

		// Calculate dynamic timeout based on content length for future requests
		timeout := Timeout // default 10 minutes
		if contentLength := resp.Header.Get("Content-Length"); contentLength != "" {
			if size := parseContentLength(contentLength); size > 0 {
				// Calculate timeout: 1 minute base + 1 minute per 50MB
				// This gives: 100MB = ~3min, 500MB = ~11min, 1GB = ~21min
				calculatedTimeout := time.Minute + time.Duration(size/(50*1024*1024))*time.Minute
				if calculatedTimeout > timeout {
					timeout = calculatedTimeout
					logging.Debug("Large file detected",
						"size_mb", size/(1024*1024),
						"calculated_timeout_minutes", int(timeout.Minutes()))
				}
			}
		}

		if resp.StatusCode != http.StatusOK {
			// Provide more specific error messages for common HTTP status codes
			// 404 errors should NOT be retried as they indicate the resource doesn't exist
			switch resp.StatusCode {
			case 404:
				return NonRetryableError{Err: fmt.Errorf("file not found (404): resource may have been moved or deleted")}
			case 403:
				return fmt.Errorf("access forbidden (403): insufficient permissions or authentication required")
			case 500:
				return fmt.Errorf("server error (500): internal server error occurred")
			case 503:
				return fmt.Errorf("service unavailable (503): server temporarily unavailable")
			default:
				return fmt.Errorf("unexpected HTTP status code: %d (%s)", resp.StatusCode, http.StatusText(resp.StatusCode))
			}
		}

		// Track expected content length for validation
		var expectedSize int64
		if contentLength := resp.Header.Get("Content-Length"); contentLength != "" {
			expectedSize = parseContentLength(contentLength)
		}

		// Copy with optional waterfall progress tracking for -vvv mode
		var written int64
		
		if verbosity >= 3 && expectedSize > 0 {
			// Waterfall progress for very verbose mode
			fileName := filepath.Base(dest)
			written, err = copyWithWaterfallProgress(out, resp.Body, expectedSize, fileName)
		} else {
			// Simple copy without progress tracking
			written, err = io.Copy(out, resp.Body)
		}
		if err != nil {
			return fmt.Errorf("failed to write downloaded data: %v", err)
		}

		// Validate file size if Content-Length was provided
		if expectedSize > 0 && written != expectedSize {
			return fmt.Errorf("incomplete download: expected %d bytes, got %d bytes", expectedSize, written)
		}

		// Ensure data is flushed to disk
		if err := out.Sync(); err != nil {
			return fmt.Errorf("failed to sync file to disk: %v", err)
		}

		logging.Debug("Download completed to temp file", "tempFile", tempDest, "size", written)
		return nil
	})

	// Handle download completion or failure
	if downloadErr != nil {
		// Clean up temp file on failure
		os.Remove(tempDest)
		// Also clean up any existing corrupt file
		if info, err := os.Stat(dest); err == nil && info.Size() == 0 {
			logging.Warn("Removing corrupt 0-byte file", "file", dest)
			os.Remove(dest)
		}
		return downloadErr
	}

	// Atomically move temp file to final destination
	if err := os.Rename(tempDest, dest); err != nil {
		os.Remove(tempDest) // cleanup on failure
		return fmt.Errorf("failed to move completed download to final location: %v", err)
	}

	// Verify the final file is not empty
	if info, err := os.Stat(dest); err == nil {
		if info.Size() == 0 {
			os.Remove(dest)
			return fmt.Errorf("download resulted in empty file, removed to prevent cache corruption")
		}
		logging.Debug("File saved successfully", "file", dest, "size", info.Size())
	}

	logging.Debug("Download completed successfully", "file", dest)
	return nil
}

// Verify checks if the given file matches the expected hash (SHA256).
func Verify(file string, expectedHash string) bool {
	actualHash := calculateHash(file)
	return strings.EqualFold(actualHash, expectedHash)
}

// InstallPendingUpdates downloads files and returns a map[name]localFilePath
func InstallPendingUpdates(downloadItems map[string]string, cfg *config.Configuration, verbosity int, reporter utils.Reporter) (map[string]string, error) {
	logging.Info("Starting pending downloads...")

	if err := os.MkdirAll(cfg.CachePath, 0755); err != nil {
		return nil, fmt.Errorf("failed to create cache directory: %v", err)
	}

	resultPaths := make(map[string]string)
	var downloadErrors []error
	successCount := 0
	failureCount := 0

	for name, url := range downloadItems {
		if url == "" {
			logging.Warn("Empty URL for package, skipping", "package", name)
			failureCount++
			continue
		}
		logging.Debug("Processing download item", "name", name, "url", url)

		// Call DownloadFile - individual failures should not stop the entire batch
		if err := DownloadFile(url, "", cfg, verbosity, reporter); err != nil {
			logging.Error("Failed to download item, continuing with remaining downloads",
				"name", name, "url", url, "error", err)
			downloadErrors = append(downloadErrors, fmt.Errorf("failed to download %s: %v", name, err))
			failureCount++
			continue // Continue processing other downloads - this is critical!
		}

		// Reconstruct exact local path the file ended up in (MUST match DownloadFile logic)
		localFilePath := getActualFilePathFromURL(url, cfg)

		// Verify the file actually exists at the expected location
		if _, err := os.Stat(localFilePath); err != nil {
			logging.Error("Downloaded file not found at expected location, continuing with remaining downloads",
				"name", name, "expected_path", localFilePath, "error", err)
			downloadErrors = append(downloadErrors, fmt.Errorf("downloaded file not found for %s at %s: %v", name, localFilePath, err))
			failureCount++
			continue
		}

		resultPaths[name] = localFilePath
		successCount++
		logging.Info("Successfully processed download item", "name", name, "path", localFilePath)
	}

	// Log summary of download results
	totalCount := len(downloadItems)
	logging.Info("Download batch completed",
		"total_items", totalCount,
		"successful", successCount,
		"failed", failureCount)

	// Return partial results even if some downloads failed - this allows installation to proceed
	// with whatever was successfully downloaded
	if len(downloadErrors) > 0 {
		var combinedErr error
		if len(downloadErrors) == 1 {
			combinedErr = downloadErrors[0]
		} else {
			combinedErr = fmt.Errorf("multiple download failures (%d out of %d items failed): %v",
				failureCount, totalCount, downloadErrors)
		}

		// Log detailed error information but still return partial results
		logging.Warn("Some downloads failed but continuing with successful ones",
			"successful_count", successCount,
			"failed_count", failureCount,
			"partial_results", len(resultPaths))

		return resultPaths, combinedErr
	}

	logging.Info("All downloads completed successfully", "count", successCount)
	return resultPaths, nil
}

// getActualFilePathFromURL reconstructs the exact full path where DownloadFile places files
// This function MUST exactly mirror the complete logic in DownloadFile including base directory selection
func getActualFilePathFromURL(url string, cfg *config.Configuration) string {
	// Set defaults exactly like DownloadFile
	if cfg.CatalogsPath == "" {
		cfg.CatalogsPath = `C:\ProgramData\ManagedInstalls\catalogs`
	}
	if cfg.CachePath == "" {
		cfg.CachePath = `C:\ProgramData\ManagedInstalls\Cache`
	}
	manifestsPath := `C:\ProgramData\ManagedInstalls\manifests`

	// Apply the same logic as DownloadFile
	lowerURL := strings.ToLower(url)
	isCatalog := strings.Contains(lowerURL, "/catalogs/")
	isManifest := strings.Contains(lowerURL, "/manifests/")
	isPackage := (!isCatalog && !isManifest)

	// Apply URL modification logic (same as DownloadFile)
	if isPackage && strings.HasPrefix(lowerURL, strings.ToLower(cfg.SoftwareRepoURL)) {
		if !strings.Contains(lowerURL, "/pkgs/") {
			trimmedRepo := strings.TrimSuffix(cfg.SoftwareRepoURL, "/")
			url = trimmedRepo + "/pkgs" + strings.TrimPrefix(url, trimmedRepo)
			lowerURL = strings.ToLower(url)
		}
	}

	// Decide base directory exactly like DownloadFile
	var baseDir string
	if isManifest {
		baseDir = manifestsPath
	} else if isCatalog {
		baseDir = cfg.CatalogsPath
	} else {
		baseDir = cfg.CachePath
	}

	// Determine subPath exactly like DownloadFile
	var subPath string
	switch {
	case isManifest:
		parts := strings.SplitN(url, "/manifests/", 2)
		if len(parts) == 2 {
			subPath = parts[1]
		} else {
			subPath = filepath.Base(url)
		}

	case isCatalog:
		parts := strings.SplitN(url, "/catalogs/", 2)
		if len(parts) == 2 {
			subPath = parts[1]
		} else {
			subPath = filepath.Base(url)
		}

	default:
		if idx := strings.Index(lowerURL, "/pkgs/"); idx >= 0 {
			subPath = url[idx+len("/pkgs/"):]
		} else {
			subPath = filepath.Base(url)
		}
	}

	// Build and return the full path exactly like DownloadFile
	dest := filepath.Join(baseDir, subPath)
	return filepath.Clean(dest)
}

// calculateHash returns the SHA256 hex of a file.
func calculateHash(path string) string {
	f, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer f.Close()
	hasher := sha256.New()
	if _, err := io.Copy(hasher, f); err != nil {
		return ""
	}
	return hex.EncodeToString(hasher.Sum(nil))
}

// parseContentLength parses the Content-Length header and returns the size in bytes
func parseContentLength(contentLength string) int64 {
	size, err := strconv.ParseInt(contentLength, 10, 64)
	if err != nil {
		return 0
	}
	return size
}

// ValidateAndCleanCache scans the cache directory for corrupt files and removes them
// This should be called periodically to prevent accumulation of corrupt downloads
func ValidateAndCleanCache(cachePath string) error {
	if cachePath == "" {
		return fmt.Errorf("cache path cannot be empty")
	}

	var corruptFiles []string
	var cleanedFiles int

	logging.Info("Starting cache validation and cleanup", "path", cachePath)

	err := filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			logging.Warn("Error accessing file during cache validation", "path", path, "error", err)
			return nil // Continue walking
		}

		// Skip directories
		if info.IsDir() {
			return nil
		}

		// Check for 0-byte files (corruption indicator)
		if info.Size() == 0 {
			corruptFiles = append(corruptFiles, path)
			logging.Warn("Found corrupt 0-byte file", "file", path, "modified", info.ModTime())

			// Remove the corrupt file
			if err := os.Remove(path); err != nil {
				logging.Error("Failed to remove corrupt file", "file", path, "error", err)
			} else {
				logging.Info("Removed corrupt file", "file", path)
				cleanedFiles++
			}
			return nil
		}

		// Additional validation for specific file types
		ext := strings.ToLower(filepath.Ext(path))
		switch ext {
		case ".nupkg":
			if err := validateNupkgFile(path); err != nil {
				logging.Warn("Found corrupt nupkg file", "file", path, "error", err)
				corruptFiles = append(corruptFiles, path)

				// Remove the corrupt nupkg
				if err := os.Remove(path); err != nil {
					logging.Error("Failed to remove corrupt nupkg", "file", path, "error", err)
				} else {
					logging.Info("Removed corrupt nupkg", "file", path)
					cleanedFiles++
				}
			}
		}

		// Check for partially downloaded files (temp files left behind)
		if strings.HasSuffix(path, ".downloading") {
			corruptFiles = append(corruptFiles, path)
			logging.Warn("Found abandoned download temp file", "file", path, "modified", info.ModTime())

			// Remove temp file
			if err := os.Remove(path); err != nil {
				logging.Error("Failed to remove temp file", "file", path, "error", err)
			} else {
				logging.Info("Removed abandoned temp file", "file", path)
				cleanedFiles++
			}
		}

		return nil
	})

	if err != nil {
		logging.Error("Cache validation walk failed", "error", err)
		return err
	}

	if len(corruptFiles) > 0 {
		logging.Warn("Cache validation completed - corruption detected",
			"corrupt_files_found", len(corruptFiles),
			"files_cleaned", cleanedFiles)

		// Log all corrupt files for analysis
		for _, file := range corruptFiles {
			logging.Info("Corrupt file details", "file", file)
		}
	} else {
		logging.Info("Cache validation completed - no corruption detected", "files_checked", cleanedFiles)
	}

	return nil
}

// validateNupkgFile performs basic validation on a .nupkg file to ensure it's not corrupt
func validateNupkgFile(path string) error {
	file, err := os.Open(path)
	if err != nil {
		return fmt.Errorf("cannot open file: %v", err)
	}
	defer file.Close()

	// Read first few bytes to check for ZIP signature (nupkg files are ZIP archives)
	header := make([]byte, 4)
	n, err := file.Read(header)
	if err != nil {
		return fmt.Errorf("cannot read file header: %v", err)
	}
	if n < 4 {
		return fmt.Errorf("file too small to be valid nupkg")
	}

	// Check for ZIP file signature (0x504B0304 = "PK\x03\x04")
	if header[0] != 0x50 || header[1] != 0x4B || header[2] != 0x03 || header[3] != 0x04 {
		return fmt.Errorf("invalid ZIP signature, not a valid nupkg file")
	}

	return nil
}

// copyWithWaterfallProgress copies data with waterfall progress display for -vvv mode
func copyWithWaterfallProgress(dst io.Writer, src io.Reader, totalSize int64, fileName string) (int64, error) {
	const bufSize = 32 * 1024 // 32KB buffer
	const updateInterval = 100 * time.Millisecond // Update every 100ms
	
	buf := make([]byte, bufSize)
	var written int64
	lastUpdate := time.Now()
	startTime := time.Now()
	
	for {
		nr, er := src.Read(buf)
		if nr > 0 {
			nw, ew := dst.Write(buf[0:nr])
			if nw < 0 || nr < nw {
				nw = 0
				if ew == nil {
					ew = fmt.Errorf("invalid write result")
				}
			}
			written += int64(nw)
			if ew != nil {
				return written, ew
			}
			if nr != nw {
				return written, io.ErrShortWrite
			}
			
			// Update waterfall progress every 100ms
			now := time.Now()
			if now.Sub(lastUpdate) >= updateInterval || written == totalSize {
				percentage := int((written * 100) / totalSize)
				elapsed := now.Sub(startTime)
				
				// Calculate speed and ETA
				var speed string
				var eta string
				if elapsed.Seconds() > 0 {
					bytesPerSecond := float64(written) / elapsed.Seconds()
					speed = formatSpeed(bytesPerSecond)
					
					if bytesPerSecond > 0 && written < totalSize {
						remainingBytes := totalSize - written
						etaSeconds := float64(remainingBytes) / bytesPerSecond
						eta = formatDuration(time.Duration(etaSeconds * float64(time.Second)))
					}
				}
				
				// Generate waterfall bar
				waterfallBar := generateWaterfallBar(percentage, fileName, speed, eta)
				fmt.Printf("\r%s", waterfallBar)
				
				lastUpdate = now
			}
		}
		if er != nil {
			if er != io.EOF {
				return written, er
			}
			break
		}
	}
	
	// Final newline to complete the waterfall display
	fmt.Println()
	
	return written, nil
}

// generateWaterfallBar creates a Unicode waterfall progress bar
func generateWaterfallBar(percentage int, fileName, speed, eta string) string {
	const barWidth = 40
	filled := (percentage * barWidth) / 100
	
	// Unicode characters for waterfall effect
	waterfall := []string{"▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"}
	empty := "░"
	
	bar := strings.Builder{}
	
	// Progress indicator
	prefix := "[DL]"
	if percentage == 100 {
		prefix = "[OK]"
	}
	
	// Build the progress bar with waterfall effect
	bar.WriteString(fmt.Sprintf("%s %s: [", prefix, truncateFileName(fileName, 20)))
	
	for i := 0; i < barWidth; i++ {
		if i < filled {
			// Use different heights for visual effect
			waterfallIndex := (i * len(waterfall)) / barWidth
			if waterfallIndex >= len(waterfall) {
				waterfallIndex = len(waterfall) - 1
			}
			bar.WriteString(waterfall[waterfallIndex])
		} else {
			bar.WriteString(empty)
		}
	}
	
	// Add percentage, speed, and ETA
	bar.WriteString(fmt.Sprintf("] %d%%", percentage))
	if speed != "" {
		bar.WriteString(fmt.Sprintf(" @ %s", speed))
	}
	if eta != "" {
		bar.WriteString(fmt.Sprintf(" ETA: %s", eta))
	}
	
	return bar.String()
}

// truncateFileName truncates a filename to fit display
func truncateFileName(fileName string, maxLen int) string {
	if len(fileName) <= maxLen {
		return fileName
	}
	return fileName[:maxLen-3] + "..."
}

// formatSpeed formats bytes per second into human-readable format
func formatSpeed(bytesPerSecond float64) string {
	if bytesPerSecond < 1024 {
		return fmt.Sprintf("%.0f B/s", bytesPerSecond)
	} else if bytesPerSecond < 1024*1024 {
		return fmt.Sprintf("%.1f KB/s", bytesPerSecond/1024)
	} else {
		return fmt.Sprintf("%.1f MB/s", bytesPerSecond/(1024*1024))
	}
}

// formatDuration formats duration into human-readable format
func formatDuration(d time.Duration) string {
	if d < time.Minute {
		return fmt.Sprintf("%ds", int(d.Seconds()))
	} else if d < time.Hour {
		return fmt.Sprintf("%dm %ds", int(d.Minutes()), int(d.Seconds())%60)
	} else {
		return fmt.Sprintf("%dh %dm", int(d.Hours()), int(d.Minutes())%60)
	}
}
