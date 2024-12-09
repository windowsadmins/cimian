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

	"github.com/windowsadmins/gorilla/pkg/auth"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/retry"
)

const (
	DefaultCachePath    = `C:\ProgramData\ManagedInstalls\Cache`
	CacheExpirationDays = 30
	Timeout             = 10 * time.Second
)

// DownloadFile handles downloading files with resumable capability and caching verification
func DownloadFile(url, dest string, cfg *config.Configuration) error {
	cfgCachePath := DefaultCachePath
	if cfg.CachePath != "" {
		cfgCachePath = cfg.CachePath
	}

	configRetry := retry.RetryConfig{MaxRetries: 3, InitialInterval: time.Second, Multiplier: 2.0}
	return retry.Retry(configRetry, func() error {
		logging.LogDownloadStart(url)
		os.MkdirAll(cfgCachePath, 0755)
		cachedFilePath := filepath.Join(cfgCachePath, filepath.Base(dest))

		// Check if the cached file exists and is valid
		if fileExists(cachedFilePath) {
			if isValidCache(cachedFilePath) {
				logging.LogVerification(cachedFilePath, "Valid")
				return copyFile(cachedFilePath, dest)
			}
			logging.LogVerification(cachedFilePath, "Expired or Invalid")
		}

		// Open the destination file with append mode for resumable download
		out, err := os.OpenFile(dest, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
		if err != nil {
			logging.Error("Failed to open destination file:", "error", err)
			return fmt.Errorf("failed to open destination file: %v", err)
		}
		defer out.Close()

		existingFileSize, err := out.Seek(0, io.SeekEnd)
		// Get file size for resuming
		if err != nil {
			logging.Error("Failed to get existing file size:", "error", err)
			return fmt.Errorf("failed to get existing file size: %v", err)
		}

		req, err := http.NewRequest("GET", url, nil)
		// Create request with Range header
		if err != nil {
			logging.Error("Failed to create HTTP request:", "error", err)
			return fmt.Errorf("failed to create HTTP request: %v", err)
		}

		// Set Authorization header based on configuration
		if cfg.ForceBasicAuth {
			authHeader, authErr := auth.GetAuthHeader()
			if authErr == nil && authHeader != "" {
				logging.Debug("Setting Authorization header for forced basic auth")
				req.Header.Set("Authorization", authHeader)
			} else {
				logging.Error("Failed to retrieve required authorization header:", "error", authErr)
				return fmt.Errorf("failed to retrieve required authorization header: %v", authErr)
			}
		} else {
			authHeader, authErr := auth.GetAuthHeader()
			if authErr == nil && authHeader != "" {
				logging.Debug("Optional Authorization header found")
				req.Header.Set("Authorization", authHeader)
			} else if authErr != nil {
				logging.Warn("No valid authorization header found:", "error", authErr)
			}
		}

		if existingFileSize > 0 {
			req.Header.Set("Range", fmt.Sprintf("bytes=%d-", existingFileSize))
		}

		client := &http.Client{
			Timeout: Timeout,
		}

		resp, err := client.Do(req)
		if err != nil {
			logging.Error("Failed to download file:", "error", err)
			return fmt.Errorf("failed to download file: %v", err)
		}
		defer resp.Body.Close()

		logging.LogDownloadComplete(dest)

		if resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusPartialContent {
			logging.Error("Unexpected HTTP status code:", "status_code", resp.StatusCode)
			return fmt.Errorf("unexpected HTTP status code: %d", resp.StatusCode)
		}

		_, err = io.Copy(out, resp.Body)
		if err != nil {
			logging.Error("Failed to write downloaded data to file:", "error", err)
			return fmt.Errorf("failed to write downloaded data to file: %v", err)
		}

		if err := copyFile(dest, cachedFilePath); err != nil {
			logging.Error("Failed to cache the downloaded file:", "error", err)
			return fmt.Errorf("failed to cache the downloaded file: %v", err)
		}

		// Cache the downloaded file
		return nil
	})
}

// Get downloads a URL and returns the body as a byte slice, with a 10-second timeout
func Get(url string, cfg *config.Configuration) ([]byte, error) {
	client := &http.Client{
		Timeout: Timeout,
	}

	// Build the request
	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}

	// Set Authorization header based on configuration
	if cfg.ForceBasicAuth {
		authHeader, authErr := auth.GetAuthHeader()
		if authErr == nil && authHeader != "" {
			logging.Debug("Setting Authorization header (ForceBasicAuth)")
			req.Header.Set("Authorization", authHeader)
		} else {
			logging.Error("Failed to retrieve required authorization header:", "error", authErr)
			return nil, fmt.Errorf("failed to retrieve required authorization header: %v", authErr)
		}
	} else {
		authHeader, authErr := auth.GetAuthHeader()
		if authErr == nil && authHeader != "" {
			logging.Debug("Optional Authorization header found")
			req.Header.Set("Authorization", authHeader)
		} else if authErr != nil {
			logging.Warn("No valid authorization header found:", "error", authErr)
		}
	}

	resp, err := client.Do(req)
	if err != nil {
		logging.Error("HTTP GET request failed", "url", url, "error", err)
		return nil, err
	}
	// Check that the request was successful
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("%s: download status code: %d", url, resp.StatusCode)
	}

	// Read the response body
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}

	return body, nil
}

// Verify compares the actual hash of a file with the provided hash
func Verify(file string, expectedHash string) bool {
	f, err := os.Open(file)
	if err != nil {
		logging.Warn("Unable to open file:", "error", err)
		return false
	}
	defer f.Close()

	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		logging.Warn("Unable to verify hash due to IO error:", "error", err)
		return false
	}

	actualHash := hex.EncodeToString(h.Sum(nil))
	return strings.EqualFold(actualHash, expectedHash)
}

// IfNeeded downloads a file if the existing one is missing or the hash does not match
func IfNeeded(filePath, url, hash string, cfg *config.Configuration) bool {
	verified := false
	if _, err := os.Stat(filePath); err == nil {
		verified = Verify(filePath, hash)
	}

	if !verified {
		logging.Info("Downloading", "url", url, "destination", filePath)
		err := DownloadFile(url, filePath, cfg)
		if err != nil {
			logging.Warn("Unable to retrieve package:", "url", url, "error", err)
			return false
		}
		verified = Verify(filePath, hash)
	}

	return verified
}

// Helper functions for caching and hash verification

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func isValidCache(path string) bool {
	fileInfo, err := os.Stat(path)
	if err != nil {
		return false
	}

	// Check if the file is expired
	if time.Since(fileInfo.ModTime()).Hours() > 24*CacheExpirationDays {
		return false
	}

	// Verify file hash (assuming SHA-256 hash is stored in metadata for comparison)
	expectedHash := calculateHash(path)
	actualHash := getStoredHash(path)
	return strings.EqualFold(expectedHash, actualHash)
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

// getStoredHash retrieves the stored hash from a .hash file next to the given path.
func getStoredHash(path string) string {
	hashFile := path + ".hash"

	// Open the hash file
	f, err := os.Open(hashFile)
	if err != nil {
		logging.Warn("Unable to open hash file:", "error", err)
		return ""
	}
	defer f.Close()

	// Read the hash from the file
	hashBytes, err := io.ReadAll(f)
	if err != nil {
		logging.Warn("Unable to read hash from file:", "error", err)
		return ""
	}

	// Return the hash as a string
	return strings.TrimSpace(string(hashBytes))
}
