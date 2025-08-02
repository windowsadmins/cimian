// pkg/logging/helpers.go - Additional helper functions for enhanced managedsoftwareupdate logging

package logging

import (
	"fmt"
	"time"
)

// Package-level convenience functions for common logging patterns

// LogInstallStart logs the start of a package installation (package-level function)
func LogInstallStart(packageName, version string) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogInstallStart(packageName, version)
}

// LogInstallProgress logs installation progress (package-level function)
func LogInstallProgress(packageName string, progress int, message string) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogInstallProgress(packageName, progress, message)
}

// LogInstallComplete logs successful completion of installation (package-level function)
func LogInstallComplete(packageName, version string, duration time.Duration) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogInstallComplete(packageName, version, duration)
}

// LogInstallFailed logs failed installation (package-level function)
func LogInstallFailed(packageName, version string, err error) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogInstallFailed(packageName, version, err)
}

// LogUninstallStart logs the start of a package uninstallation
func LogUninstallStart(packageName, version string) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("uninstall", "start", "started",
		fmt.Sprintf("Starting uninstallation of %s %s", packageName, version),
		WithPackage(packageName, version),
		WithLevel("INFO"))
}

// LogUninstallComplete logs successful completion of uninstallation
func LogUninstallComplete(packageName, version string, duration time.Duration) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("uninstall", "complete", "completed",
		fmt.Sprintf("Successfully uninstalled %s %s", packageName, version),
		WithPackage(packageName, version),
		WithDuration(duration),
		WithLevel("INFO"))
}

// LogUninstallFailed logs failed uninstallation
func LogUninstallFailed(packageName, version string, err error) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("uninstall", "complete", "failed",
		fmt.Sprintf("Failed to uninstall %s %s", packageName, version),
		WithPackage(packageName, version),
		WithError(err),
		WithLevel("ERROR"))
}

// LogDownloadStart logs the start of a package download
func LogDownloadStart(packageName, version, url string) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("download", "start", "started",
		fmt.Sprintf("Starting download of %s %s", packageName, version),
		WithPackage(packageName, version),
		WithContext("download_url", url),
		WithLevel("INFO"))
}

// LogDownloadProgress logs download progress
func LogDownloadProgress(packageName string, progress int, bytesDownloaded, totalBytes int64) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("download", "progress", "downloading",
		fmt.Sprintf("Downloading %s: %d%% (%d/%d bytes)", packageName, progress, bytesDownloaded, totalBytes),
		WithPackage(packageName, ""),
		WithProgress(progress),
		WithContext("bytes_downloaded", bytesDownloaded),
		WithContext("total_bytes", totalBytes),
		WithLevel("DEBUG"))
}

// LogDownloadComplete logs successful completion of download
func LogDownloadComplete(packageName, version string, duration time.Duration, filePath string, fileSize int64) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("download", "complete", "completed",
		fmt.Sprintf("Successfully downloaded %s %s (%d bytes)", packageName, version, fileSize),
		WithPackage(packageName, version),
		WithDuration(duration),
		WithContext("file_path", filePath),
		WithContext("file_size", fileSize),
		WithLevel("INFO"))
}

// LogDownloadFailed logs failed download
func LogDownloadFailed(packageName, version string, err error, url string) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("download", "complete", "failed",
		fmt.Sprintf("Failed to download %s %s", packageName, version),
		WithPackage(packageName, version),
		WithContext("download_url", url),
		WithError(err),
		WithLevel("ERROR"))
}

// LogManifestEvent logs manifest-related events
func LogManifestEvent(action, status, message string, opts ...EventOption) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("manifest", action, status, message, opts...)
}

// LogCacheEvent logs cache-related events
func LogCacheEvent(action, status, message string, opts ...EventOption) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("cache", action, status, message, opts...)
}

// LogCatalogEvent logs catalog-related events
func LogCatalogEvent(action, status, message string, opts ...EventOption) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("catalog", action, status, message, opts...)
}

// LogDependencyEvent logs dependency resolution events
func LogDependencyEvent(action, status, message string, opts ...EventOption) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("dependency", action, status, message, opts...)
}

// LogBlockingEvent logs blocking application events
func LogBlockingEvent(packageName string, blockingApps []string) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	return instance.LogEvent("blocking", "check", "blocked",
		fmt.Sprintf("Package %s blocked by running applications", packageName),
		WithPackage(packageName, ""),
		WithContext("blocking_apps", blockingApps),
		WithLevel("WARNING"))
}

// LogPreflightEvent logs preflight script events
func LogPreflightEvent(status, message string, duration time.Duration, err error) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	opts := []EventOption{
		WithDuration(duration),
		WithLevel("INFO"),
	}
	if err != nil {
		opts = append(opts, WithError(err), WithLevel("ERROR"))
	}
	return instance.LogEvent("preflight", "execute", status, message, opts...)
}

// LogPostflightEvent logs postflight script events
func LogPostflightEvent(status, message string, duration time.Duration, err error) error {
	if instance == nil {
		return fmt.Errorf("logger not initialized")
	}
	opts := []EventOption{
		WithDuration(duration),
		WithLevel("INFO"),
	}
	if err != nil {
		opts = append(opts, WithError(err), WithLevel("ERROR"))
	}
	return instance.LogEvent("postflight", "execute", status, message, opts...)
}
