# Package Integrity Verification in Cimian

Cimian implements strict package integrity verification using SHA256 hashes, following the architectural pattern of Munki where the catalog serves as the "source of truth."

## Architecture Overview

The integrity verification process flows from the catalog definition through the installation orchestration and is enforced at the download layer.

### 1. Source of Truth: The Catalog
**File:** `pkg/catalog/catalog.go`

The `InstallerItem` struct maps the `hash` field from the YAML catalog. This defines the expected state of the package.

```go
type InstallerItem struct {
    Type        string   `yaml:"type"`
    Location    string   `yaml:"location"`
    Hash        string   `yaml:"hash"` // <--- The expected SHA256 hash
    // ...
}
```

### 2. Orchestration: The Hand-off
**File:** `pkg/process/process.go`

When `managedsoftwareupdate` initiates an installation, the `downloadItemFile` function extracts the hash from the catalog item and passes it to the download subsystem.

```go
func downloadItemFile(item catalog.Item, ...) (string, error) {
    // ...
    // Passes item.Installer.Hash to the downloader
    if err := download.DownloadFile(fullURL, ..., item.Installer.Hash); err != nil {
        return "", fmt.Errorf("failed to download %s: %v", item.Name, err)
    }
    // ...
}
```

### 3. Enforcement: The Download Layer
**File:** `pkg/download/download.go`

The `DownloadFile` function enforces integrity at two critical points:

1.  **Pre-flight Check (Cache Validation):**
    If the file already exists in the local cache, its hash is verified against the catalog's expected hash *before* it is used.
    -   **Match:** The download is skipped, and the cached file is used.
    -   **Mismatch:** The cached file is deleted immediately, and a fresh download is initiated.

2.  **Post-download Check (New File Validation):**
    Immediately after a file is downloaded, its hash is calculated and compared to the expected hash.
    -   **Match:** The file is accepted and moved to the final destination.
    -   **Mismatch:** The file is deleted immediately to prevent corrupted installers from persisting on disk.

```go
func DownloadFile(..., expectedHash ...string) error {
    // ...
    // 1. Check existing file
    if info, err := os.Stat(dest); err == nil {
        if validationHash != "" {
            if Verify(dest, validationHash) {
                return nil // Hash matches, skip download
            } else {
                os.Remove(dest) // Hash mismatch, delete and re-download
            }
        }
    }

    // ... (Download happens here) ...

    // 2. Verify new download
    if validationHash != "" {
        if !Verify(dest, validationHash) {
            os.Remove(dest) // Security check failed, delete immediately
            return fmt.Errorf("downloaded file hash validation failed...")
        }
    }
    return nil
}
```

## Security Benefits

This architecture ensures that:
*   **Man-in-the-middle attacks** are detected (if HTTPS were to fail or be bypassed).
*   **CDN corruption** or truncated downloads are rejected.
*   **Cache corruption** is automatically self-healed by re-downloading.
