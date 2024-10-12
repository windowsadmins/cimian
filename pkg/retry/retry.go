
package retry

import (
    "time"
    "log"
    "fmt"
)

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

        log.Printf("[RETRY] Attempt %d/%d failed: %v. Retrying in %s...", attempt, config.MaxRetries, err, interval)
        time.Sleep(interval)
        interval = time.Duration(float64(interval) * config.Multiplier)
    }

    return fmt.Errorf("action failed after %d attempts", config.MaxRetries)
}
