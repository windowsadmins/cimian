// pkg/download/download.go

package download

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/retry"
	"github.com/windowsadmins/gorilla/pkg/utils"
)

const (
	DefaultCachePath    = `C:\ProgramData\ManagedInstalls\Cache`
	ManifestBasePath    = `C:\ProgramData\ManagedInstalls\manifests`
	CatalogBasePath     = `C:\ProgramData\ManagedInstalls\catalogs`
	CacheExpirationDays = 30
	Timeout             = 10 * time.Second
)

// DownloadFile downloads the specified URL to a destination file, ensuring the folder structure matches the server truth.
func DownloadFile(url, dest string, cfg *config.Configuration) error {
	if url == "" {
		return fmt.Errorf("invalid parameters: url cannot be empty")
	}

	// Determine the base directory for manifests, catalogs, or cache
	var basePath string
	var subPath string

	switch {
	case strings.Contains(url, "/manifests/"):
		basePath = ManifestBasePath
		subPath = strings.SplitN(url, "/manifests/", 2)[1] // Preserve only the part after "/manifests/"
	case strings.Contains(url, "/catalogs/"):
		basePath = CatalogBasePath
		subPath = strings.SplitN(url, "/catalogs/", 2)[1] // Preserve only the part after "/catalogs/"
	default:
		basePath = DefaultCachePath
		subPath = filepath.Base(url) // For packages, use only the filename
	}

	// Construct the final destination path
	dest = filepath.Join(basePath, subPath)

	// Log the resolved destination path
	logging.Debug("Resolved download destination", "url", url, "destination", dest)

	// Ensure the full directory structure exists
	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		return fmt.Errorf("failed to create directory structure: %v", err)
	}

	configRetry := retry.RetryConfig{MaxRetries: 3, InitialInterval: time.Second, Multiplier: 2.0}
	return retry.Retry(configRetry, func() error {
		logging.Info("Starting download", "url", url, "destination", dest)

		// Overwrite manifest or catalog files directly
		out, err := os.Create(dest)
		if err != nil {
			return fmt.Errorf("failed to open destination file: %v", err)
		}
		defer out.Close()

		// Prepare the HTTP request
		req, err := utils.NewAuthenticatedRequest("GET", url, nil)
		if err != nil {
			return fmt.Errorf("failed to prepare HTTP request: %v", err)
		}

		// Perform the download
		client := &http.Client{Timeout: Timeout}
		resp, err := client.Do(req)
		if err != nil {
			return fmt.Errorf("failed to perform HTTP request: %v", err)
		}
		defer resp.Body.Close()

		// Verify successful response
		if resp.StatusCode != http.StatusOK {
			return fmt.Errorf("unexpected HTTP status code: %d", resp.StatusCode)
		}

		// Write downloaded data to file
		_, err = io.Copy(out, resp.Body)
		if err != nil {
			return fmt.Errorf("failed to write downloaded data: %v", err)
		}

		logging.Info("Download completed successfully", "file", dest)
		return nil
	})
}

// cleanFolder removes all existing files and folders in the given path.
func cleanFolder(path string) error {
	// Ensure the base path exists; if not, create it
	if _, err := os.Stat(path); os.IsNotExist(err) {
		if err := os.MkdirAll(path, 0755); err != nil {
			return fmt.Errorf("failed to create directory: %v", err)
		}
		return nil // Nothing to clean; path is newly created
	}

	// Walk the directory and clean contents without removing the base path
	entries, err := os.ReadDir(path)
	if err != nil {
		return fmt.Errorf("failed to list directory contents: %v", err)
	}

	for _, entry := range entries {
		entryPath := filepath.Join(path, entry.Name())

		// Remove file or directory
		if entry.IsDir() {
			if err := os.RemoveAll(entryPath); err != nil {
				return fmt.Errorf("failed to remove directory: %v", err)
			}
		} else {
			if err := os.Remove(entryPath); err != nil {
				return fmt.Errorf("failed to remove file: %v", err)
			}
		}
	}

	return nil
}

// Verify checks if the given file matches the expected hash.
func Verify(file string, expectedHash string) bool {
	actualHash := calculateHash(file)
	return strings.EqualFold(actualHash, expectedHash)
}

// isValidCache verifies the cached file against its hash file.
func isValidCache(filePath, hashFilePath string) bool {
	expectedHash, err := os.ReadFile(hashFilePath)
	if err != nil {
		logging.Warn("Failed to read hash file", "file", hashFilePath, "error", err)
		return false
	}

	actualHash := calculateHash(filePath)
	if !strings.EqualFold(strings.TrimSpace(string(expectedHash)), actualHash) {
		logging.Warn("Hash mismatch for cached file", "file", filePath)
		return false
	}

	fileInfo, err := os.Stat(filePath)
	if err != nil || fileInfo.Size() == 0 {
		logging.Warn("Failed to retrieve file info or file size invalid", "file", filePath)
		return false
	}

	if time.Since(fileInfo.ModTime()).Hours() > 24*CacheExpirationDays {
		logging.Warn("Cached file is expired", "file", filePath)
		return false
	}

	return true
}

// InstallPendingUpdates downloads files based on a map of file names and URLs.
func InstallPendingUpdates(downloadItems map[string]string, cfg *config.Configuration) error {
	logging.Info("Starting pending downloads...")

	for name, url := range downloadItems {
		destPath := filepath.Join(cfg.CachePath, filepath.Base(url))

		logging.Info("Downloading", "name", name, "url", url, "destination", destPath)
		if err := DownloadFile(url, destPath, cfg); err != nil {
			logging.Warn("Failed to download", "name", name, "error", err)
			return fmt.Errorf("failed to download %s: %v", name, err)
		}
		logging.Info("Successfully downloaded", "name", name)
	}

	return nil
}

// Helper functions

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func calculateHash(path string) string {
	file, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer file.Close()

	hasher := sha256.New()
	if _, err := io.Copy(hasher, file); err != nil {
		return ""
	}

	return hex.EncodeToString(hasher.Sum(nil))
}

func copyFile(src, dest string) error {
	input, err := os.Open(src)
	if err != nil {
		return err
	}
	defer input.Close()

	output, err := os.Create(dest)
	if err != nil {
		return err
	}
	defer output.Close()

	_, err = io.Copy(output, input)
	return err
}
