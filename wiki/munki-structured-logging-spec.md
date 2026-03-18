# Structured JSON Logging for Munki

**Status:** Specification for Munki fork  
**Origin:** Ported from Cimian's SessionLogger system  
**Date:** March 2026

## Problem Statement

Munki's logging is plain-text in `/Library/Managed Installs/Logs/ManagedSoftwareUpdate.log`. This makes it difficult to:
- Query logs programmatically (osquery, Splunk, jq)
- Correlate events within a single run session
- Aggregate install/failure statistics across a fleet
- Track session duration and outcomes

## Design

### Directory Structure

```
/Library/Managed Installs/Logs/
├── ManagedSoftwareUpdate.log          # Existing plain-text log (unchanged)
└── json/
    ├── 2026-03-15/
    │   ├── session-100000.json        # Session summary
    │   ├── session-100000.jsonl       # Event stream (JSONL)
    │   ├── session-143000.json
    │   └── session-143000.jsonl
    ├── 2026-03-16/
    │   └── ...
    ├── sessions.json                  # Rolling aggregate of recent sessions
    └── events.json                    # Rolling aggregate of recent events
```

### Session File (`session-HHMMSS.json`)

Written once at session end:

```json
{
  "session_id": "2026-03-15T10:00:00-0700_abc123",
  "started": "2026-03-15T10:00:00-0700",
  "ended": "2026-03-15T10:04:32-0700",
  "duration_seconds": 272,
  "mode": "auto",
  "outcome": "completed",
  "manifest": "site_default",
  "catalog_count": 2,
  "items_installed": 3,
  "items_updated": 1,
  "items_removed": 0,
  "items_failed": 0,
  "items_skipped": 2,
  "total_download_bytes": 157286400,
  "errors": [],
  "warnings": ["LoopGuard: Suppressed GoogleChrome"],
  "machine_id": "C02X1234ABCD",
  "os_version": "14.3.1",
  "munki_version": "7.1.0"
}
```

### Event Stream (`session-HHMMSS.jsonl`)

One JSON object per line, appended in real-time:

```jsonl
{"ts":"2026-03-15T10:00:00-0700","level":"INFO","event":"session_start","mode":"auto"}
{"ts":"2026-03-15T10:00:01-0700","level":"INFO","event":"manifest_load","manifest":"site_default","item_count":15}
{"ts":"2026-03-15T10:00:03-0700","level":"INFO","event":"catalog_load","catalog":"production","item_count":142}
{"ts":"2026-03-15T10:00:05-0700","level":"INFO","event":"install_start","name":"Firefox","version":"124.0","size":98000000}
{"ts":"2026-03-15T10:02:15-0700","level":"INFO","event":"install_end","name":"Firefox","version":"124.0","result":"success","duration_seconds":130}
{"ts":"2026-03-15T10:02:16-0700","level":"WARN","event":"loop_suppressed","name":"GoogleChrome","reason":"rapid-fire","attempts":4}
{"ts":"2026-03-15T10:04:32-0700","level":"INFO","event":"session_end","outcome":"completed","installed":3,"updated":1,"failed":0}
```

### Aggregate Files

**`sessions.json`** — Rolling array of the last 100 session summaries (for quick fleet queries):

```json
[
  {"session_id":"...","started":"...","outcome":"completed","items_installed":3},
  {"session_id":"...","started":"...","outcome":"failed","items_installed":0}
]
```

**`events.json`** — Rolling array of the last 500 events (for recent activity):

```json
[
  {"ts":"...","level":"INFO","event":"install_end","name":"Firefox","result":"success"},
  {"ts":"...","level":"ERROR","event":"install_end","name":"Zoom","result":"failed","error":"exit code 1603"}
]
```

### Retention Policy

- Daily directories older than 30 days are automatically deleted at session start
- `sessions.json` is capped at 100 entries (oldest removed first)
- `events.json` is capped at 500 entries (oldest removed first)
- Plain-text log behavior unchanged

### Query Examples

**osquery:**
```sql
-- Find machines with failed installs in last 24h
SELECT * FROM file
WHERE path LIKE '/Library/Managed Installs/Logs/json/sessions.json'
AND json_extract(data, '$[0].outcome') = 'failed';
```

**jq:**
```bash
# All failed installs in the last session
jq '.[] | select(.event == "install_end" and .result == "failed")' \
  /Library/Managed\ Installs/Logs/json/2026-03-15/session-100000.jsonl

# Session durations over 5 minutes
jq '.[] | select(.duration_seconds > 300)' \
  /Library/Managed\ Installs/Logs/json/sessions.json
```

## Implementation Notes

- Use Python's `json` module for serialization (no external dependencies)
- JSONL files should be opened in append mode and flushed after each write for crash safety
- Session summary is written atomically at session end (write-temp-then-rename)
- The existing plain-text log remains the primary log; JSON logging is additive
- Consider a ManagedPreferences key `EnableJSONLogging` (Boolean, default true) for opt-out
- File permissions should match existing log permissions (root:wheel, 0644)
