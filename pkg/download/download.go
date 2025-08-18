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
	Timeout             = 30 * time.Second // Reduced from 10 minutes to 30 seconds
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

	return retry.Retry(configRetry, func() error {
		logging.Debug("Starting download", "url", url, "destination", dest)

		out, err := os.Create(dest)
		if err != nil {
			return fmt.Errorf("failed to open destination file: %v", err)
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

		if _, err = io.Copy(out, resp.Body); err != nil {
			return fmt.Errorf("failed to write downloaded data: %v", err)
		}

		logging.Debug("File saved", "file", dest)
		logging.Debug("Download completed successfully", "file", dest)
		return nil
	})
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
