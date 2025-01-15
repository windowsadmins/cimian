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
	CacheExpirationDays = 30
	Timeout             = 10 * time.Second
)

// DownloadFile replaces any hardcoded paths with your config paths and ensures packages go to cfg.CachePath.
func DownloadFile(url, dest string, cfg *config.Configuration) error {
	if url == "" {
		return fmt.Errorf("invalid parameters: url cannot be empty")
	}

	// Default paths if config is empty
	catalogsPath := cfg.CatalogsPath
	if catalogsPath == "" {
		catalogsPath = `C:\ProgramData\ManagedInstalls\catalogs`
	}
	cachePath := cfg.CachePath
	if cachePath == "" {
		cachePath = `C:\ProgramData\ManagedInstalls\Cache`
	}
	const defaultManifestsPath = `C:\ProgramData\ManagedInstalls\manifests`

	isManifest := strings.Contains(url, "/manifests/")
	isCatalog := strings.Contains(url, "/catalogs/")
	isPackage := !isManifest && !isCatalog

	// Insert /pkgs if it's a package URL but missing
	if isPackage && strings.HasPrefix(url, cfg.SoftwareRepoURL) && !strings.Contains(url, "/pkgs/") {
		url = strings.TrimRight(cfg.SoftwareRepoURL, "/") + "/pkgs" + strings.TrimPrefix(url, cfg.SoftwareRepoURL)
	}

	var basePath, subPath string
	switch {
	case isManifest:
		basePath = defaultManifestsPath
		subPath = strings.SplitN(url, "/manifests/", 2)[1]
	case isCatalog:
		basePath = catalogsPath
		subPath = strings.SplitN(url, "/catalogs/", 2)[1]
	default:
		basePath = cachePath
		subPath = filepath.Base(url)
	}

	dest = filepath.Join(basePath, subPath)
	logging.Debug("Resolved download destination", "url", url, "destination", dest)

	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		return fmt.Errorf("failed to create directory structure: %v", err)
	}

	configRetry := retry.RetryConfig{MaxRetries: 3, InitialInterval: time.Second, Multiplier: 2.0}
	return retry.Retry(configRetry, func() error {
		logging.Info("Starting download", "url", url, "destination", dest)

		out, err := os.Create(dest)
		if err != nil {
			return fmt.Errorf("failed to open destination file: %v", err)
		}
		defer out.Close()

		req, err := utils.NewAuthenticatedRequest("GET", url, nil)
		if err != nil {
			return fmt.Errorf("failed to prepare HTTP request: %v", err)
		}

		client := &http.Client{Timeout: Timeout}
		resp, err := client.Do(req)
		if err != nil {
			return fmt.Errorf("failed to perform HTTP request: %v", err)
		}
		defer resp.Body.Close()

		if resp.StatusCode != http.StatusOK {
			return fmt.Errorf("unexpected HTTP status code: %d", resp.StatusCode)
		}

		if _, err = io.Copy(out, resp.Body); err != nil {
			return fmt.Errorf("failed to write downloaded data: %v", err)
		}

		logging.Debug("File saved", "file", dest)
		logging.Info("Download completed successfully", "file", dest)
		return nil
	})
}

// Verify checks if the given file matches the expected hash.
func Verify(file string, expectedHash string) bool {
	actualHash := calculateHash(file)
	return strings.EqualFold(actualHash, expectedHash)
}

// InstallPendingUpdates downloads files based on a map of file names and URLs.
func InstallPendingUpdates(downloadItems map[string]string, cfg *config.Configuration) error {
	logging.Info("Starting pending downloads...")

	if err := os.MkdirAll(cfg.CachePath, 0755); err != nil {
		return fmt.Errorf("failed to create cache directory: %v", err)
	}

	for name, url := range downloadItems {
		if url == "" {
			logging.Warn("Empty URL for package", "package", name)
			continue
		}
		if strings.HasPrefix(url, "/") {
			url = cfg.SoftwareRepoURL + "/pkgs" + url
		}
		targetFile := filepath.Join(cfg.CachePath, filepath.Base(url))
		logging.Debug("Processing download item", "name", name, "url", url, "destination", targetFile)

		if err := DownloadFile(url, targetFile, cfg); err != nil {
			logging.Error("Failed to download", "name", name, "error", err)
			return fmt.Errorf("failed to download %s: %v", name, err)
		}
		logging.Info("Successfully downloaded", "name", name, "target", targetFile)
	}

	return nil
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
