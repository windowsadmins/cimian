package utils

import (
	"fmt"
	"io"
	"net/http"
	"time"

	"github.com/windowsadmins/gorilla/pkg/auth"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/logging"
)

const DefaultTimeout = 10 * time.Second

// AuthenticatedGet performs an HTTP GET request with a Basic Auth header if force_basic_auth is true.
func AuthenticatedGet(url string) ([]byte, error) {
	client := &http.Client{Timeout: DefaultTimeout}

	// Load configuration
	cfg, err := config.Load()
	if err != nil {
		logging.Error("Failed to load configuration", "error", err)
		return nil, fmt.Errorf("failed to load configuration: %v", err)
	}

	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}

	// Conditionally add Authorization Header
	if cfg.ForceBasicAuth {
		authHeader, authErr := auth.GetAuthHeader()
		if authErr == nil && authHeader != "" {
			req.Header.Set("Authorization", authHeader)
		} else {
			logging.Error("Failed to retrieve Authorization header", "error", authErr)
			return nil, fmt.Errorf("authorization header missing: %v", authErr)
		}
	} else {
		logging.Info("Skipping Basic Auth as force_basic_auth is false")
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("download status code: %d", resp.StatusCode)
	}

	return io.ReadAll(resp.Body)
}
