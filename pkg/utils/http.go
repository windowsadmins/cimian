// pkg/utils/http.go

package utils

import (
	"fmt"
	"io"
	"net/http"
	"time"

	"github.com/windowsadmins/gorilla/pkg/auth"
	"github.com/windowsadmins/gorilla/pkg/logging"
)

const DefaultTimeout = 10 * time.Second

func NewAuthenticatedRequest(method, url string, body io.Reader) (*http.Request, error) {
	req, err := http.NewRequest(method, url, body)
	if err != nil {
		return nil, err
	}

	authHeader, authErr := auth.GetAuthHeader()
	if authErr != nil || authHeader == "" {
		logging.Warn("No valid Authorization header found, proceeding without authentication.")
	} else {
		req.Header.Set("Authorization", "Basic "+authHeader)
		logging.Debug("Authorization header included")
	}

	return req, nil
}

func DoAuthenticatedGet(url string) ([]byte, error) {
	client := &http.Client{Timeout: DefaultTimeout}

	req, err := NewAuthenticatedRequest("GET", url, nil)
	if err != nil {
		return nil, err
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
