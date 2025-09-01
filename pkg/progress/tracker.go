// pkg/progress/progress.go - Comprehensive progress tracking for Cimian with GUI integration
package progress

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// ProgressTracker manages overall installation progress for GUI integration
type ProgressTracker struct {
	mu                sync.RWMutex
	items             map[string]*ItemProgress
	totalItems        int
	completedItems    int
	currentItem       string
	sessionID         string
	logDir            string
	statusUpdateChan  chan StatusUpdate
	watchers          []chan StatusUpdate
	verbosity         int
}

// ItemProgress tracks progress for a single item
type ItemProgress struct {
	Name              string            `json:"name"`
	DisplayName       string            `json:"display_name"`
	Status            ItemStatus        `json:"status"`
	Phase             string            `json:"phase"`
	Progress          int               `json:"progress"`         // 0-100
	Error             string            `json:"error,omitempty"`
	Warning           string            `json:"warning,omitempty"`
	StartTime         time.Time         `json:"start_time"`
	EndTime           *time.Time        `json:"end_time,omitempty"`
	Duration          int64             `json:"duration_ms"`
	Version           string            `json:"version,omitempty"`
	InstallerType     string            `json:"installer_type"`  // MSI, EXE, NUPKG, PWSH, MSIX
	Action            string            `json:"action"`          // install, update, uninstall
	Dependencies      []string          `json:"dependencies,omitempty"`
	CurrentDependency string            `json:"current_dependency,omitempty"`
	DownloadProgress  *DownloadProgress `json:"download_progress,omitempty"`
	InstallProgress   *InstallPhaseInfo `json:"install_progress,omitempty"`
	Metadata          map[string]string `json:"metadata,omitempty"`
}

// ItemStatus represents the current status of an item
type ItemStatus string

const (
	StatusPending     ItemStatus = "pending"
	StatusDownloading ItemStatus = "downloading"
	StatusInstalling  ItemStatus = "installing"
	StatusCompleted   ItemStatus = "completed"
	StatusFailed      ItemStatus = "failed"
	StatusWarning     ItemStatus = "warning"
	StatusSkipped     ItemStatus = "skipped"
	StatusKnowledge   ItemStatus = "knowledge"  // For C# Dialog integration - item status knowledge
)

// DownloadProgress tracks file download progress for waterfall display
type DownloadProgress struct {
	URL           string    `json:"url"`
	LocalPath     string    `json:"local_path"`
	TotalBytes    int64     `json:"total_bytes"`
	DownloadedBytes int64   `json:"downloaded_bytes"`
	Percentage    int       `json:"percentage"`
	Speed         string    `json:"speed"`
	ETA           string    `json:"eta"`
	StartTime     time.Time `json:"start_time"`
	WaterfallBars []string  `json:"waterfall_bars,omitempty"` // For -vvv mode
}

// InstallPhaseInfo tracks installation phases for different installer types
type InstallPhaseInfo struct {
	CurrentPhase  string    `json:"current_phase"`
	Phases        []string  `json:"phases"`
	PhaseIndex    int       `json:"phase_index"`
	StartTime     time.Time `json:"start_time"`
	Emoji         string    `json:"emoji"`
	Description   string    `json:"description"`
}

// StatusUpdate represents a status change for GUI integration
type StatusUpdate struct {
	Type      string                 `json:"type"`      // "item_update", "session_update", "waterfall"
	ItemName  string                 `json:"item_name,omitempty"`
	Data      map[string]interface{} `json:"data"`
	Timestamp time.Time              `json:"timestamp"`
}

// NewProgressTracker creates a new progress tracker
func NewProgressTracker(sessionID string, logDir string, verbosity int) *ProgressTracker {
	tracker := &ProgressTracker{
		items:            make(map[string]*ItemProgress),
		sessionID:        sessionID,
		logDir:           logDir,
		statusUpdateChan: make(chan StatusUpdate, 100),
		watchers:         make([]chan StatusUpdate, 0),
		verbosity:        verbosity,
	}
	
	// Start background goroutine to handle status updates
	go tracker.handleStatusUpdates()
	
	return tracker
}

// AddItem registers a new item for tracking
func (pt *ProgressTracker) AddItem(name, displayName, action, installerType, version string, dependencies []string) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	item := &ItemProgress{
		Name:          name,
		DisplayName:   displayName,
		Status:        StatusPending,
		Phase:         "Queued",
		Progress:      0,
		StartTime:     time.Now(),
		Version:       version,
		InstallerType: installerType,
		Action:        action,
		Dependencies:  dependencies,
		Metadata:      make(map[string]string),
	}

	pt.items[name] = item
	pt.totalItems++

	// Send update to GUI
	pt.sendUpdate("item_update", name, map[string]interface{}{
		"status": item.Status,
		"phase":  item.Phase,
		"action": action,
		"type":   installerType,
	})
}

// StartItemDownload begins download tracking for an item
func (pt *ProgressTracker) StartItemDownload(itemName, url, localPath string, totalBytes int64) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists {
		item.Status = StatusDownloading
		item.Phase = "Downloading"
		item.DownloadProgress = &DownloadProgress{
			URL:           url,
			LocalPath:     localPath,
			TotalBytes:    totalBytes,
			StartTime:     time.Now(),
			WaterfallBars: make([]string, 0),
		}

		pt.currentItem = itemName
		pt.sendUpdate("item_update", itemName, map[string]interface{}{
			"status":    item.Status,
			"phase":     item.Phase,
			"download":  item.DownloadProgress,
		})
	}
}

// UpdateDownloadProgress updates download progress and creates waterfall display
func (pt *ProgressTracker) UpdateDownloadProgress(itemName string, downloadedBytes int64) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists && item.DownloadProgress != nil {
		dl := item.DownloadProgress
		dl.DownloadedBytes = downloadedBytes
		
		if dl.TotalBytes > 0 {
			dl.Percentage = int((downloadedBytes * 100) / dl.TotalBytes)
		}

		// Calculate speed and ETA
		elapsed := time.Since(dl.StartTime)
		if elapsed.Seconds() > 0 {
			bytesPerSecond := float64(downloadedBytes) / elapsed.Seconds()
			dl.Speed = formatSpeed(bytesPerSecond)
			
			if bytesPerSecond > 0 && dl.TotalBytes > downloadedBytes {
				remainingBytes := dl.TotalBytes - downloadedBytes
				etaSeconds := float64(remainingBytes) / bytesPerSecond
				dl.ETA = formatDuration(time.Duration(etaSeconds * float64(time.Second)))
			}
		}

		// Generate waterfall progress bar for -vvv mode
		if pt.verbosity >= 3 {
			dl.WaterfallBars = append(dl.WaterfallBars, pt.generateWaterfallBar(dl.Percentage, itemName))
			// Keep only last 5 bars to prevent memory buildup
			if len(dl.WaterfallBars) > 5 {
				dl.WaterfallBars = dl.WaterfallBars[len(dl.WaterfallBars)-5:]
			}

			// Print waterfall to console in verbose mode
			if len(dl.WaterfallBars) > 0 {
				fmt.Printf("\r%s", dl.WaterfallBars[len(dl.WaterfallBars)-1])
			}
		}

		item.Progress = dl.Percentage

		pt.sendUpdate("waterfall", itemName, map[string]interface{}{
			"download": dl,
			"progress": dl.Percentage,
		})
	}
}

// StartItemInstall begins installation tracking for an item
func (pt *ProgressTracker) StartItemInstall(itemName string) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists {
		item.Status = StatusInstalling
		phases := pt.getInstallPhases(item.InstallerType)
		item.InstallProgress = &InstallPhaseInfo{
			CurrentPhase: phases[0],
			Phases:       phases,
			PhaseIndex:   0,
			StartTime:    time.Now(),
			Emoji:        pt.getPhaseEmoji(phases[0]),
			Description:  pt.getPhaseDescription(phases[0], item.InstallerType),
		}
		item.Phase = phases[0]
		item.Progress = 10

		pt.currentItem = itemName
		pt.sendUpdate("item_update", itemName, map[string]interface{}{
			"status":  item.Status,
			"phase":   item.Phase,
			"install": item.InstallProgress,
		})
	}
}

// UpdateInstallPhase moves to the next installation phase
func (pt *ProgressTracker) UpdateInstallPhase(itemName, newPhase string) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists && item.InstallProgress != nil {
		install := item.InstallProgress
		
		// Find phase index
		for i, phase := range install.Phases {
			if phase == newPhase {
				install.PhaseIndex = i
				install.CurrentPhase = newPhase
				install.Emoji = pt.getPhaseEmoji(newPhase)
				install.Description = pt.getPhaseDescription(newPhase, item.InstallerType)
				break
			}
		}

		item.Phase = newPhase
		item.Progress = 20 + (install.PhaseIndex * 20) // Rough progress estimation

		pt.sendUpdate("item_update", itemName, map[string]interface{}{
			"status":  item.Status,
			"phase":   item.Phase,
			"install": item.InstallProgress,
		})
	}
}

// CompleteItem marks an item as completed
func (pt *ProgressTracker) CompleteItem(itemName string) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists {
		now := time.Now()
		item.Status = StatusCompleted
		item.Phase = "Completed"
		item.Progress = 100
		item.EndTime = &now
		item.Duration = now.Sub(item.StartTime).Milliseconds()

		pt.completedItems++

		pt.sendUpdate("item_update", itemName, map[string]interface{}{
			"status":   item.Status,
			"phase":    item.Phase,
			"progress": 100,
			"duration": item.Duration,
		})

		pt.sendSessionUpdate()
	}
}

// FailItem marks an item as failed with error information
func (pt *ProgressTracker) FailItem(itemName, errorMsg string) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists {
		now := time.Now()
		item.Status = StatusFailed
		item.Phase = "Failed"
		item.Error = errorMsg
		item.EndTime = &now
		item.Duration = now.Sub(item.StartTime).Milliseconds()

		pt.completedItems++

		pt.sendUpdate("item_update", itemName, map[string]interface{}{
			"status":   item.Status,
			"phase":    item.Phase,
			"error":    errorMsg,
			"duration": item.Duration,
		})

		pt.sendSessionUpdate()
	}
}

// WarnItem adds a warning to an item
func (pt *ProgressTracker) WarnItem(itemName, warning string) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists {
		item.Warning = warning
		if item.Status == StatusCompleted {
			item.Status = StatusWarning
		}

		pt.sendUpdate("item_update", itemName, map[string]interface{}{
			"status":  item.Status,
			"warning": warning,
		})
	}
}

// SetItemKnowledge sets the knowledge status for C# Dialog integration
func (pt *ProgressTracker) SetItemKnowledge(itemName string, isInstalled bool, currentVersion, availableVersion string) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	if item, exists := pt.items[itemName]; exists {
		item.Status = StatusKnowledge
		if item.Metadata == nil {
			item.Metadata = make(map[string]string)
		}
		item.Metadata["installed"] = fmt.Sprintf("%t", isInstalled)
		item.Metadata["current_version"] = currentVersion
		item.Metadata["available_version"] = availableVersion
		item.Metadata["needs_update"] = fmt.Sprintf("%t", isInstalled && currentVersion != availableVersion)

		pt.sendUpdate("knowledge", itemName, map[string]interface{}{
			"installed":         isInstalled,
			"current_version":   currentVersion,
			"available_version": availableVersion,
			"needs_update":      isInstalled && currentVersion != availableVersion,
		})
	}
}

// GetSessionSummary returns overall session progress
func (pt *ProgressTracker) GetSessionSummary() map[string]interface{} {
	pt.mu.RLock()
	defer pt.mu.RUnlock()

	var pending, downloading, installing, completed, failed, warnings int
	
	for _, item := range pt.items {
		switch item.Status {
		case StatusPending:
			pending++
		case StatusDownloading:
			downloading++
		case StatusInstalling:
			installing++
		case StatusCompleted:
			completed++
		case StatusFailed:
			failed++
		case StatusWarning:
			warnings++
		}
	}

	return map[string]interface{}{
		"total_items":     pt.totalItems,
		"completed_items": pt.completedItems,
		"pending":         pending,
		"downloading":     downloading,
		"installing":      installing,
		"completed":       completed,
		"failed":          failed,
		"warnings":        warnings,
		"current_item":    pt.currentItem,
		"session_id":      pt.sessionID,
	}
}

// AddWatcher adds a channel to receive status updates (for GUI integration)
func (pt *ProgressTracker) AddWatcher() <-chan StatusUpdate {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	watcher := make(chan StatusUpdate, 50)
	pt.watchers = append(pt.watchers, watcher)
	return watcher
}

// RemoveWatcher removes a watcher channel
func (pt *ProgressTracker) RemoveWatcher(watcher <-chan StatusUpdate) {
	pt.mu.Lock()
	defer pt.mu.Unlock()

	for i, w := range pt.watchers {
		if w == watcher {
			close(w)
			pt.watchers = append(pt.watchers[:i], pt.watchers[i+1:]...)
			break
		}
	}
}

// ExportToJSON exports current progress to JSON for GUI consumption
func (pt *ProgressTracker) ExportToJSON() ([]byte, error) {
	pt.mu.RLock()
	defer pt.mu.RUnlock()

	data := map[string]interface{}{
		"session_id": pt.sessionID,
		"summary":    pt.GetSessionSummary(),
		"items":      pt.items,
		"timestamp":  time.Now(),
	}

	return json.MarshalIndent(data, "", "  ")
}

// SaveProgressFile saves progress to file for GUI integration
func (pt *ProgressTracker) SaveProgressFile() error {
	if pt.logDir == "" {
		return nil // No log directory specified
	}

	data, err := pt.ExportToJSON()
	if err != nil {
		return err
	}

	progressFile := filepath.Join(pt.logDir, "progress.json")
	return os.WriteFile(progressFile, data, 0644)
}

// Private helper methods

func (pt *ProgressTracker) sendUpdate(updateType, itemName string, data map[string]interface{}) {
	update := StatusUpdate{
		Type:      updateType,
		ItemName:  itemName,
		Data:      data,
		Timestamp: time.Now(),
	}

	select {
	case pt.statusUpdateChan <- update:
	default:
		// Channel full, skip update to prevent blocking
	}
}

func (pt *ProgressTracker) sendSessionUpdate() {
	pt.sendUpdate("session_update", "", pt.GetSessionSummary())
}

func (pt *ProgressTracker) handleStatusUpdates() {
	for update := range pt.statusUpdateChan {
		// Broadcast to all watchers
		for _, watcher := range pt.watchers {
			select {
			case watcher <- update:
			default:
				// Watcher channel full, skip
			}
		}

		// Save progress file for GUI integration
		pt.SaveProgressFile()
	}
}

func (pt *ProgressTracker) getInstallPhases(installerType string) []string {
	switch strings.ToLower(installerType) {
	case "msi":
		return []string{"Preparing", "Validating", "Extracting", "Installing", "Configuring", "Finalizing"}
	case "exe":
		return []string{"Preparing", "Launching", "Installing", "Configuring", "Finalizing"}
	case "nupkg":
		return []string{"Preparing", "Extracting", "Dependencies", "Installing", "Scripts", "Finalizing"}
	case "pwsh", "powershell":
		return []string{"Preparing", "Loading", "Executing", "Finalizing"}
	case "msix":
		return []string{"Preparing", "Validating", "Registering", "Installing", "Finalizing"}
	default:
		return []string{"Preparing", "Processing", "Installing", "Finalizing"}
	}
}

func (pt *ProgressTracker) getPhaseEmoji(phase string) string {
	switch strings.ToLower(phase) {
	case "preparing":
		return "[PREP]"
	case "downloading":
		return "[DOWN]"
	case "validating":
		return "[VALID]"
	case "extracting":
		return "[EXTR]"
	case "installing":
		return "[INST]"
	case "configuring":
		return "[CONF]"
	case "finalizing":
		return "[DONE]"
	case "launching":
		return "[LAUNCH]"
	case "dependencies":
		return "[LINK]"
	case "scripts":
		return "[DOC]"
	case "loading":
		return "[LOAD]"
	case "executing":
		return "[EXEC]"
	case "registering":
		return "[REG]"
	case "processing":
		return "[PROC]"
	case "completed":
		return "[DONE]"
	case "failed":
		return "[FAIL]"
	default:
		return "[PROC]"
	}
}

func (pt *ProgressTracker) getPhaseDescription(phase, installerType string) string {
	switch strings.ToLower(phase) {
	case "preparing":
		return "Preparing installation environment"
	case "validating":
		return "Validating installer package"
	case "extracting":
		return "Extracting installation files"
	case "installing":
		return fmt.Sprintf("Running %s installer", strings.ToUpper(installerType))
	case "configuring":
		return "Configuring application settings"
	case "finalizing":
		return "Completing installation"
	case "launching":
		return "Launching installer executable"
	case "dependencies":
		return "Processing dependencies"
	case "scripts":
		return "Running installation scripts"
	case "loading":
		return "Loading PowerShell script"
	case "executing":
		return "Executing PowerShell commands"
	case "registering":
		return "Registering MSIX package"
	case "processing":
		return "Processing installation"
	default:
		return "Working..."
	}
}

func (pt *ProgressTracker) generateWaterfallBar(percentage int, itemName string) string {
	const barWidth = 50
	filled := (percentage * barWidth) / 100
	
	bar := strings.Builder{}
	bar.WriteString(fmt.Sprintf("\r[PKG] %s: [", itemName))
	
	for i := 0; i < barWidth; i++ {
		if i < filled {
			bar.WriteString("█")
		} else {
			bar.WriteString("░")
		}
	}
	
	bar.WriteString(fmt.Sprintf("] %d%%", percentage))
	return bar.String()
}

func formatSpeed(bytesPerSecond float64) string {
	if bytesPerSecond < 1024 {
		return fmt.Sprintf("%.0f B/s", bytesPerSecond)
	} else if bytesPerSecond < 1024*1024 {
		return fmt.Sprintf("%.1f KB/s", bytesPerSecond/1024)
	} else {
		return fmt.Sprintf("%.1f MB/s", bytesPerSecond/(1024*1024))
	}
}

func formatDuration(d time.Duration) string {
	if d < time.Minute {
		return fmt.Sprintf("%ds", int(d.Seconds()))
	} else if d < time.Hour {
		return fmt.Sprintf("%dm %ds", int(d.Minutes()), int(d.Seconds())%60)
	} else {
		return fmt.Sprintf("%dh %dm", int(d.Hours()), int(d.Minutes())%60)
	}
}

// ProgressReader wraps an io.Reader to track download progress
type ProgressReader struct {
	reader   io.Reader
	total    int64
	read     int64
	tracker  *ProgressTracker
	itemName string
}

// NewProgressReader creates a new progress tracking reader
func NewProgressReader(reader io.Reader, total int64, tracker *ProgressTracker, itemName string) *ProgressReader {
	return &ProgressReader{
		reader:   reader,
		total:    total,
		tracker:  tracker,
		itemName: itemName,
	}
}

// Read implements io.Reader interface with progress tracking
func (pr *ProgressReader) Read(p []byte) (n int, err error) {
	n, err = pr.reader.Read(p)
	pr.read += int64(n)
	
	if pr.tracker != nil {
		pr.tracker.UpdateDownloadProgress(pr.itemName, pr.read)
	}
	
	return n, err
}
