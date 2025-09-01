// Enhanced catalog loader with session-level caching and better logging
// This provides session-level caching (5-minute cache for catalogs as requested)

package catalog

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/utils"
	"gopkg.in/yaml.v3"
)

// SessionCache provides session-level caching for catalogs
type SessionCache struct {
	catalogs   map[string]CachedCatalogData
	mutex      sync.RWMutex
	expiration time.Duration
}

// CachedCatalogData represents cached catalog information
type CachedCatalogData struct {
	Items     map[string]Item
	CachedAt  time.Time
	ExpiresAt time.Time
}

var (
	sessionCache     *SessionCache
	sessionCacheOnce sync.Once
)

// getSessionCache returns the singleton session cache instance
func getSessionCache() *SessionCache {
	sessionCacheOnce.Do(func() {
		sessionCache = &SessionCache{
			catalogs:   make(map[string]CachedCatalogData),
			expiration: 5 * time.Minute, // 5-minute cache as requested
		}
	})
	return sessionCache
}

// AuthenticatedGetEnhanced retrieves and parses catalogs with session-level caching
func AuthenticatedGetEnhanced(cfg config.Configuration) map[int]map[string]Item {
	startTime := time.Now()
	cache := getSessionCache()

	// catalogMap holds parsed catalog data from all configured catalogs.
	var catalogMap = make(map[int]map[string]Item)
	catalogCount := 0

	// Catch unexpected failures
	defer func() {
		if r := recover(); r != nil {
			logging.Error("Catalog processing failed with panic", "error", r)
			os.Exit(1)
		}
	}()

	// Ensure at least one catalog is defined
	if len(cfg.Catalogs) < 1 {
		logging.Error("Unable to continue, no catalogs assigned", "catalogs", cfg.Catalogs)
		return catalogMap
	}


	// Loop through each catalog name in config.Catalogs
	for _, catalogName := range cfg.Catalogs {
		catalogStartTime := time.Now()
		catalogCount++

		// Check session cache first
		cache.mutex.RLock()
		cachedData, exists := cache.catalogs[catalogName]
		cache.mutex.RUnlock()

		var indexedItems map[string]Item

		if exists && time.Now().Before(cachedData.ExpiresAt) {
			// Use cached data
			indexedItems = cachedData.Items
			logging.Info("Using catalog", "name", catalogName, "items", len(indexedItems))
		} else {
			// Download fresh catalog
			logging.Info("Downloading catalog", "name", catalogName)

			// Build the catalog URL and local destination path
			catalogURL := fmt.Sprintf("%s/catalogs/%s.yaml",
				strings.TrimRight(cfg.SoftwareRepoURL, "/"),
				catalogName)
			catalogFilePath := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catalogName+".yaml")

			// Download the catalog file
			if err := download.DownloadFile(catalogURL, catalogFilePath, &cfg, 0, utils.NewNoOpReporter()); err != nil {
				logging.Error("Failed to download catalog", "url", catalogURL, "error", err)
				continue
			}

			// Read the downloaded YAML file
			yamlFile, err := os.ReadFile(catalogFilePath)
			if err != nil {
				logging.Error("Failed to read downloaded catalog file", "path", catalogFilePath, "error", err)
				continue
			}

			// Parse the catalog
			type catalogWrapper struct {
				Items []Item `yaml:"items"`
			}

			var wrapper catalogWrapper
			if err := yaml.Unmarshal(yamlFile, &wrapper); err != nil {
				logging.Error("unable to parse YAML", "path", catalogFilePath, "error", err)
				continue
			}

			// Convert the slice into a map keyed by item.Name
			indexedItems = make(map[string]Item)
			for _, it := range wrapper.Items {
				if it.Name != "" {
					indexedItems[it.Name] = it
				}
			}

			// Cache the result for this session
			cache.mutex.Lock()
			cache.catalogs[catalogName] = CachedCatalogData{
				Items:     indexedItems,
				CachedAt:  time.Now(),
				ExpiresAt: time.Now().Add(cache.expiration),
			}
			cache.mutex.Unlock()

			logging.Info("Downloaded and cached", "name", catalogName,
				"items", len(indexedItems),
				"duration_ms", time.Since(catalogStartTime).Milliseconds())
		}

		catalogMap[catalogCount] = indexedItems
	}

	logging.Info("Enhanced catalog loading completed",
		"catalogs", catalogCount,
		"total_duration_ms", time.Since(startTime).Milliseconds())

	return catalogMap
}

// ClearSessionCache clears the session cache (useful for testing)
func ClearSessionCache() {
	cache := getSessionCache()
	cache.mutex.Lock()
	defer cache.mutex.Unlock()
	cache.catalogs = make(map[string]CachedCatalogData)
}

// GetCacheStatus returns information about the current cache status
func GetCacheStatus() map[string]interface{} {
	cache := getSessionCache()
	cache.mutex.RLock()
	defer cache.mutex.RUnlock()

	status := make(map[string]interface{})
	status["cache_count"] = len(cache.catalogs)
	status["expiration_minutes"] = cache.expiration.Minutes()

	catalogStatus := make(map[string]interface{})
	for name, data := range cache.catalogs {
		catalogStatus[name] = map[string]interface{}{
			"items":      len(data.Items),
			"cached_at":  data.CachedAt.Format(time.RFC3339),
			"expires_at": data.ExpiresAt.Format(time.RFC3339),
			"is_expired": time.Now().After(data.ExpiresAt),
		}
	}
	status["catalogs"] = catalogStatus

	return status
}
