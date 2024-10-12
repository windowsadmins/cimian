
package download

import (
	"github.com/rodchristiansen/gorilla/pkg/retry"
	"github.com/rodchristiansen/gorilla/pkg/logging"
    "crypto/sha256"
    "encoding/hex"
    "fmt"
    "io"
    "net/http"
    "os"
    "path/filepath"
    "time"
    "log"
)

const (
    CachePath = `C:\ProgramData\ManagedInstalls\Cache`
    CacheExpirationDays = 30
)

// DownloadFile handles downloading files with resumable capability and caching verification
func DownloadFile(url, dest string) error {
	config := retry.RetryConfig{MaxRetries: 3, InitialInterval: time.Second, Multiplier: 2.0}
	return retry.Retry(config, func() error {
	logging.LogDownloadStart(url)
    os.MkdirAll(CachePath, 0755)
    cachedFilePath := filepath.Join(CachePath, filepath.Base(dest))

    // Check if the cached file exists and is valid
    if fileExists(cachedFilePath) {
        if isValidCache(cachedFilePath) {
		logging.LogVerification(cachedFilePath, "Valid") {
            log.Printf("Using cached file: %s", cachedFilePath)
            return copyFile(cachedFilePath, dest)
        }
        logging.LogVerification(cachedFilePath, "Expired or Invalid")
    }

    // Open the destination file with append mode for resumable download
    out, err := os.OpenFile(dest, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
    if err != nil {
        return fmt.Errorf("failed to open destination file: %v", err)
    }
    defer out.Close()

    // Get file size for resuming
    existingFileSize, err := out.Seek(0, io.SeekEnd)
    if err != nil {
        return fmt.Errorf("failed to get existing file size: %v", err)
    }

    // Create request with Range header
    req, err := http.NewRequest("GET", url, nil)
    if err != nil {
        return fmt.Errorf("failed to create HTTP request: %v", err)
    }
    if existingFileSize > 0 {
        req.Header.Set("Range", fmt.Sprintf("bytes=%d-", existingFileSize))
    }

    resp, err := http.DefaultClient.Do(req)
    if err != nil {
        return fmt.Errorf("failed to download file: %v", err)
    }
    defer resp.Body.Close()
	logging.LogDownloadComplete(dest)

    if resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusPartialContent {
        return fmt.Errorf("unexpected HTTP status code: %d", resp.StatusCode)
    }

    // Write the response body to the destination file
    _, err = io.Copy(out, resp.Body)
    if err != nil {
        return fmt.Errorf("failed to write downloaded data to file: %v", err)
    }

    // Cache the downloaded file
    if err := copyFile(dest, cachedFilePath); err != nil {
        return fmt.Errorf("failed to cache the downloaded file: %v", err)
    }

    return nil })
}

// fileExists checks if a file exists at the given path
func fileExists(path string) bool {
    _, err := os.Stat(path)
    return err == nil
}

// isValidCache checks if the cached file is still valid based on expiration and hash verification
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
    actualHash := getStoredHash(path) // This function would retrieve the hash stored during the original download
    return expectedHash == actualHash
}

// calculateHash computes the SHA-256 hash of a file
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

// copyFile copies a file from src to dest
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
