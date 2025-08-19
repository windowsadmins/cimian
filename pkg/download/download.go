// pkg/download/download.go - functions for downloading files from repo.

package download

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
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
	Timeout             = 10 * time.Minute // Increased back to 10 minutes for large files
	LargeFileThreshold  = 100 * 1024 * 1024 // 100MB threshold for large files
)

// DownloadFile retrieves a file from `url` and saves it to the correct local path.
// If the URL indicates a catalog or manifest, it goes in the corresponding folder.
// Otherwise, it's treated as a package and goes to cfg.CachePath.
func DownloadFile(url, unusedDest string, cfg *config.Configuration) error {
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

		written, err := io.Copy(out, resp.Body)
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
func InstallPendingUpdates(downloadItems map[string]string, cfg *config.Configuration) (map[string]string, error) {
	logging.Info("Starting pending downloads...")

	if err := os.MkdirAll(cfg.CachePath, 0755); err != nil {
		return nil, fmt.Errorf("failed to create cache directory: %v", err)
	}

	resultPaths := make(map[string]string)

	for name, url := range downloadItems {
		if url == "" {
			logging.Warn("Empty URL for package", "package", name)
			continue
		}
		logging.Debug("Processing download item", "name", name, "url", url)

		// Call DownloadFile
		if err := DownloadFile(url, "", cfg); err != nil {
			logging.Error("Failed to download", "name", name, "error", err)

			// Check if this is a non-retryable error and propagate it accordingly
			var nonRetryableErr NonRetryableError
			if errors.As(err, &nonRetryableErr) {
				return nil, NonRetryableError{Err: fmt.Errorf("failed to download %s: %v", name, err)}
			}

			return nil, fmt.Errorf("failed to download %s: %v", name, err)
		}

		// Reconstruct exact local path the file ended up in (MUST match DownloadFile logic)
		subPath := getSubPathFromURL(url, cfg)
		localFilePath := filepath.Join(cfg.CachePath, subPath)
		localFilePath = filepath.Clean(localFilePath)

		resultPaths[name] = localFilePath
		logging.Info("Successfully downloaded", "name", name, "path", localFilePath)
	}

	return resultPaths, nil
}

// getSubPathFromURL mirrors the logic in DownloadFile to consistently generate paths
func getSubPathFromURL(url string, cfg *config.Configuration) string {
	lowerURL := strings.ToLower(url)
	var subPath string

	switch {
	case strings.Contains(lowerURL, "/catalogs/"):
		subPath = strings.SplitN(url, "/catalogs/", 2)[1]
	case strings.Contains(lowerURL, "/manifests/"):
		subPath = strings.SplitN(url, "/manifests/", 2)[1]
	case strings.Contains(lowerURL, "/pkgs/"):
		idx := strings.Index(lowerURL, "/pkgs/")
		subPath = url[idx+len("/pkgs/"):]
	default:
		subPath = filepath.Base(url)
	}
	return filepath.FromSlash(subPath)
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
