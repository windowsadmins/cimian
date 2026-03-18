# Install Loop Prevention for Munki (LoopGuard)

**Status:** Specification for Munki fork  
**Origin:** Ported from Cimian's LoopGuard system  
**Date:** March 2026

## Problem Statement

Munki can get stuck in install loops when a package reports "needs install" after every run — due to version mismatches, broken `installcheck_script`, or a damaged installer. Each run downloads, installs, and logs the same item repeatedly, wasting bandwidth, CPU, and admin attention.

## Design

### State File

Store loop state in a JSON file at `/Library/Managed Installs/LoopGuard/state.json`:

```json
{
  "catalog_fingerprint": "sha256:abc123...",
  "items": {
    "GoogleChrome": {
      "attempt_count": 3,
      "first_attempt": "2026-03-15T10:00:00Z",
      "last_attempt": "2026-03-15T10:45:00Z",
      "suppressed_until": "2026-03-15T22:45:00Z",
      "versions_seen": ["122.0.6261.94", "122.0.6261.94"],
      "reason": "rapid-fire"
    }
  }
}
```

### Detection Thresholds

| Pattern | Trigger | Backoff | Description |
|---------|---------|---------|-------------|
| **Rapid-fire** | 3+ attempts within 2 hours | 12h hold | Same item keeps cycling |
| **Version-stuck** | 3+ attempts, same version, within 3 consecutive sessions | 6h hold | Version never changes |
| **Chronic** | 5+ total attempts across 5+ sessions | 24h hold | Persistent failure |
| **Hard limit** | 8+ total attempts | Indefinite (manual clear) | Admin must intervene |

### Auto-Clear

When the catalog fingerprint changes (i.e., admin pushed a new catalog), all suppression state is cleared. This allows fixes to take effect immediately without manual intervention.

### Bootstrap Mode

During first-run / bootstrap (detected by checking if `InstallInfo.plist` doesn't exist or has 0 processed installs), LoopGuard thresholds are relaxed:
- Rapid-fire threshold raised to 5 (from 3)
- No indefinite holds during bootstrap
- Bootstrap mode auto-expires after 2 hours

### CLI Commands

```bash
# View current suppression state
managedsoftwareupdate --loop-status

# Clear suppression for a specific item
managedsoftwareupdate --loop-clear GoogleChrome

# Clear all suppressions
managedsoftwareupdate --loop-clear-all

# Force install despite suppression (one-time)
managedsoftwareupdate --loop-override GoogleChrome
```

### Integration Points

1. **Pre-install check:** Before each item install, check `state.json`. If suppressed, skip and log warning.
2. **Post-install record:** After each install attempt, increment counters and update timestamps.
3. **Catalog refresh:** On catalog download, compute SHA256 fingerprint and compare to stored value. If changed, clear all state.
4. **Reporting:** Include loop-suppressed items in `ManagedInstallReport.plist` under a new `LoopSuppressedItems` key.

### Logging

```
WARNING: LoopGuard: Suppressing GoogleChrome - 4 attempts in 1.5h (rapid-fire pattern, held until 2026-03-15 22:45)
INFO: LoopGuard: Catalog fingerprint changed - clearing all suppressions
INFO: LoopGuard: Cleared suppression for GoogleChrome (admin override)
```

## Implementation Notes

- State file should be atomic-write (write to temp, rename) to avoid corruption during concurrent runs
- Munki's `updatecheck` module is the natural integration point for pre-install checks
- The `installationstate` module should record post-install attempts
- Consider adding a ManagedPreferences key `SuppressLoopGuard` (Boolean) for admins who want to disable it entirely
