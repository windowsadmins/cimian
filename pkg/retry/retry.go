// pkg/retry/retry.go - functions for retrying actions with exponential backoff.

package retry

import (
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/logging"
)

// NonRetryableError interface for errors that should not be retried
type NonRetryableError interface {
	error
	Unwrap() error
}

// RetryConfig defines the configuration for retry attempts
type RetryConfig struct {
	MaxRetries      int
	InitialInterval time.Duration
	Multiplier      float64
}

// Retry retries a given function with exponential backoff
func Retry(config RetryConfig, action func() error) error {
	interval := config.InitialInterval

	for attempt := 1; attempt <= config.MaxRetries; attempt++ {
		err := action()
		if err == nil {
			return nil
		}

		// Check if this is a non-retryable error
		var nonRetryableErr NonRetryableError
		if errors.As(err, &nonRetryableErr) {
			logging.LogStructured(logging.LevelWarn,
				fmt.Sprintf("Non-retryable error encountered: %s", err.Error()),
				map[string]interface{}{
					"level":         "RETRY",
					"attempt":       attempt,
					"non_retryable": true,
				})
			return err
		}

		// Improve error message for common 404 errors
		errorMsg := err.Error()
		if strings.Contains(strings.ToLower(errorMsg), "404") {
			if strings.Contains(strings.ToLower(errorMsg), "unexpected http status code: 404") {
				errorMsg = "file not found (404): resource may have been moved or deleted"
			}
		}

		if attempt < config.MaxRetries {
			// Log retry attempts with custom properties for RETRY level
			logging.LogStructured(logging.LevelWarn,
				fmt.Sprintf("Attempt %d/%d failed: %s. Retrying in %s...",
					attempt, config.MaxRetries, errorMsg, interval.String()),
				map[string]interface{}{
					"level":        "RETRY",
					"attempt":      attempt,
					"max_attempts": config.MaxRetries,
					"retry_delay":  interval.String(),
				})
		} else {
			// Log final failure
			logging.LogStructured(logging.LevelWarn,
				fmt.Sprintf("Attempt %d/%d failed: %s. No more retries.",
					attempt, config.MaxRetries, errorMsg),
				map[string]interface{}{
					"level":         "RETRY",
					"attempt":       attempt,
					"max_attempts":  config.MaxRetries,
					"final_failure": true,
				})
		}

		time.Sleep(interval)
		interval = time.Duration(float64(interval) * config.Multiplier)
	}

	return fmt.Errorf("action failed after %d attempts", config.MaxRetries)
}
