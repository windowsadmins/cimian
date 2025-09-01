// pkg/progress/gui.go - GUI integration interfaces for CimianStatus and csharpDialog
package progress

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

// GUIInterface defines the interface for GUI integration
type GUIInterface interface {
	UpdateProgress(itemName string, progress int, phase string) error
	UpdateStatus(itemName string, status string, message string) error
	ShowWaterfall(itemName string, waterfallData []string) error
	SetItemKnowledge(itemName string, knowledge ItemKnowledge) error
	UpdateSessionSummary(summary SessionSummary) error
}

// ItemKnowledge represents what we know about an item for C# Dialog integration
type ItemKnowledge struct {
	Name             string `json:"name"`
	DisplayName      string `json:"display_name"`
	IsInstalled      bool   `json:"is_installed"`
	CurrentVersion   string `json:"current_version"`
	AvailableVersion string `json:"available_version"`
	NeedsUpdate      bool   `json:"needs_update"`
	InstallMethod    string `json:"install_method"`
	Description      string `json:"description,omitempty"`
	Icon             string `json:"icon,omitempty"`
	Category         string `json:"category,omitempty"`
	Size             string `json:"size,omitempty"`
}

// SessionSummary represents overall session status
type SessionSummary struct {
	SessionID        string `json:"session_id"`
	StartTime        string `json:"start_time"`
	CurrentTime      string `json:"current_time"`
	TotalItems       int    `json:"total_items"`
	CompletedItems   int    `json:"completed_items"`
	FailedItems      int    `json:"failed_items"`
	CurrentItem      string `json:"current_item"`
	OverallProgress  int    `json:"overall_progress"`
	EstimatedTimeLeft string `json:"estimated_time_left"`
}

// CimianStatusGUI implements GUIInterface for the existing CimianStatus WPF application
type CimianStatusGUI struct {
	progressFile string
	httpPort     int
	httpServer   *http.Server
}

// NewCimianStatusGUI creates a new CimianStatus GUI interface
func NewCimianStatusGUI(logDir string) *CimianStatusGUI {
	gui := &CimianStatusGUI{
		progressFile: filepath.Join(logDir, "gui_progress.json"),
		httpPort:     8765, // Default port for CimianStatus communication
	}
	
	gui.startHTTPServer()
	return gui
}

// UpdateProgress updates the progress for a specific item
func (gui *CimianStatusGUI) UpdateProgress(itemName string, progress int, phase string) error {
	data := map[string]interface{}{
		"type":      "progress_update",
		"item_name": itemName,
		"progress":  progress,
		"phase":     phase,
		"timestamp": time.Now().Format(time.RFC3339),
	}
	
	return gui.writeGUIUpdate(data)
}

// UpdateStatus updates the status for a specific item
func (gui *CimianStatusGUI) UpdateStatus(itemName string, status string, message string) error {
	data := map[string]interface{}{
		"type":      "status_update",
		"item_name": itemName,
		"status":    status,
		"message":   message,
		"timestamp": time.Now().Format(time.RFC3339),
	}
	
	return gui.writeGUIUpdate(data)
}

// ShowWaterfall displays waterfall progress in the GUI
func (gui *CimianStatusGUI) ShowWaterfall(itemName string, waterfallData []string) error {
	data := map[string]interface{}{
		"type":          "waterfall_update",
		"item_name":     itemName,
		"waterfall_data": waterfallData,
		"timestamp":     time.Now().Format(time.RFC3339),
	}
	
	return gui.writeGUIUpdate(data)
}

// SetItemKnowledge sets the knowledge status for an item (C# Dialog integration)
func (gui *CimianStatusGUI) SetItemKnowledge(itemName string, knowledge ItemKnowledge) error {
	data := map[string]interface{}{
		"type":      "knowledge_update",
		"item_name": itemName,
		"knowledge": knowledge,
		"timestamp": time.Now().Format(time.RFC3339),
	}
	
	return gui.writeGUIUpdate(data)
}

// UpdateSessionSummary updates the overall session summary
func (gui *CimianStatusGUI) UpdateSessionSummary(summary SessionSummary) error {
	data := map[string]interface{}{
		"type":      "session_summary",
		"summary":   summary,
		"timestamp": time.Now().Format(time.RFC3339),
	}
	
	return gui.writeGUIUpdate(data)
}

// writeGUIUpdate writes an update to the GUI progress file
func (gui *CimianStatusGUI) writeGUIUpdate(data map[string]interface{}) error {
	// Ensure directory exists
	dir := filepath.Dir(gui.progressFile)
	if err := os.MkdirAll(dir, 0755); err != nil {
		return err
	}
	
	// Read existing data
	var updates []map[string]interface{}
	if content, err := os.ReadFile(gui.progressFile); err == nil {
		json.Unmarshal(content, &updates)
	}
	
	// Add new update
	updates = append(updates, data)
	
	// Keep only last 100 updates to prevent file from growing too large
	if len(updates) > 100 {
		updates = updates[len(updates)-100:]
	}
	
	// Write back to file
	jsonData, err := json.MarshalIndent(updates, "", "  ")
	if err != nil {
		return err
	}
	
	return os.WriteFile(gui.progressFile, jsonData, 0644)
}

// startHTTPServer starts an HTTP server for real-time GUI communication
func (gui *CimianStatusGUI) startHTTPServer() {
	mux := http.NewServeMux()
	
	// Endpoint for CimianStatus to get current progress
	mux.HandleFunc("/progress", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Access-Control-Allow-Origin", "*")
		
		content, err := os.ReadFile(gui.progressFile)
		if err != nil {
			http.Error(w, "Progress file not found", http.StatusNotFound)
			return
		}
		
		w.Write(content)
	})
	
	// Server-Sent Events endpoint for real-time updates
	mux.HandleFunc("/events", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		w.Header().Set("Cache-Control", "no-cache")
		w.Header().Set("Connection", "keep-alive")
		w.Header().Set("Access-Control-Allow-Origin", "*")
		
		// This would be enhanced to provide real-time updates
		// For now, just send the current progress
		content, err := os.ReadFile(gui.progressFile)
		if err == nil {
			fmt.Fprintf(w, "data: %s\n\n", content)
		}
		
		if flusher, ok := w.(http.Flusher); ok {
			flusher.Flush()
		}
	})
	
	gui.httpServer = &http.Server{
		Addr:    fmt.Sprintf(":%d", gui.httpPort),
		Handler: mux,
	}
	
	go func() {
		if err := gui.httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			// Log error but don't fail - GUI integration is optional
			fmt.Printf("GUI HTTP server error: %v\n", err)
		}
	}()
}

// Close shuts down the GUI interface
func (gui *CimianStatusGUI) Close() error {
	if gui.httpServer != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		return gui.httpServer.Shutdown(ctx)
	}
	return nil
}

// CSDialogGUI implements GUIInterface for C# Dialog integration (future use)
type CSDialogGUI struct {
	dialogPath   string
	commandQueue chan DialogCommand
}

// DialogCommand represents a command to send to C# Dialog
type DialogCommand struct {
	Command string                 `json:"command"`
	Data    map[string]interface{} `json:"data"`
}

// NewCSDialogGUI creates a new C# Dialog GUI interface
func NewCSDialogGUI(dialogPath string) *CSDialogGUI {
	gui := &CSDialogGUI{
		dialogPath:   dialogPath,
		commandQueue: make(chan DialogCommand, 100),
	}
	
	go gui.processCommands()
	return gui
}

// UpdateProgress updates progress in C# Dialog
func (gui *CSDialogGUI) UpdateProgress(itemName string, progress int, phase string) error {
	cmd := DialogCommand{
		Command: "update_progress",
		Data: map[string]interface{}{
			"item_name": itemName,
			"progress":  progress,
			"phase":     phase,
		},
	}
	
	select {
	case gui.commandQueue <- cmd:
		return nil
	default:
		return fmt.Errorf("command queue full")
	}
}

// UpdateStatus updates status in C# Dialog
func (gui *CSDialogGUI) UpdateStatus(itemName string, status string, message string) error {
	cmd := DialogCommand{
		Command: "update_status",
		Data: map[string]interface{}{
			"item_name": itemName,
			"status":    status,
			"message":   message,
		},
	}
	
	select {
	case gui.commandQueue <- cmd:
		return nil
	default:
		return fmt.Errorf("command queue full")
	}
}

// ShowWaterfall shows waterfall progress in C# Dialog
func (gui *CSDialogGUI) ShowWaterfall(itemName string, waterfallData []string) error {
	cmd := DialogCommand{
		Command: "show_waterfall",
		Data: map[string]interface{}{
			"item_name":      itemName,
			"waterfall_data": waterfallData,
		},
	}
	
	select {
	case gui.commandQueue <- cmd:
		return nil
	default:
		return fmt.Errorf("command queue full")
	}
}

// SetItemKnowledge sets item knowledge in C# Dialog
func (gui *CSDialogGUI) SetItemKnowledge(itemName string, knowledge ItemKnowledge) error {
	cmd := DialogCommand{
		Command: "set_knowledge",
		Data: map[string]interface{}{
			"item_name": itemName,
			"knowledge": knowledge,
		},
	}
	
	select {
	case gui.commandQueue <- cmd:
		return nil
	default:
		return fmt.Errorf("command queue full")
	}
}

// UpdateSessionSummary updates session summary in C# Dialog
func (gui *CSDialogGUI) UpdateSessionSummary(summary SessionSummary) error {
	cmd := DialogCommand{
		Command: "update_session",
		Data: map[string]interface{}{
			"summary": summary,
		},
	}
	
	select {
	case gui.commandQueue <- cmd:
		return nil
	default:
		return fmt.Errorf("command queue full")
	}
}

// processCommands processes commands to send to C# Dialog
func (gui *CSDialogGUI) processCommands() {
	for cmd := range gui.commandQueue {
		// This would send commands to the C# Dialog application
		// Implementation depends on how csharpdialog accepts commands
		// Could be via named pipes, HTTP, files, etc.
		
		// For now, just write to a command file that C# Dialog can monitor
		cmdFile := filepath.Join(filepath.Dir(gui.dialogPath), "dialog_commands.json")
		
		jsonData, err := json.Marshal(cmd)
		if err != nil {
			continue
		}
		
		// Append command to file
		file, err := os.OpenFile(cmdFile, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
		if err != nil {
			continue
		}
		
		file.Write(jsonData)
		file.WriteString("\n")
		file.Close()
	}
}

// MultiGUI combines multiple GUI interfaces
type MultiGUI struct {
	interfaces []GUIInterface
}

// NewMultiGUI creates a new multi-GUI interface
func NewMultiGUI(interfaces ...GUIInterface) *MultiGUI {
	return &MultiGUI{interfaces: interfaces}
}

// UpdateProgress updates progress in all GUI interfaces
func (mgui *MultiGUI) UpdateProgress(itemName string, progress int, phase string) error {
	var lastError error
	for _, gui := range mgui.interfaces {
		if err := gui.UpdateProgress(itemName, progress, phase); err != nil {
			lastError = err
		}
	}
	return lastError
}

// UpdateStatus updates status in all GUI interfaces
func (mgui *MultiGUI) UpdateStatus(itemName string, status string, message string) error {
	var lastError error
	for _, gui := range mgui.interfaces {
		if err := gui.UpdateStatus(itemName, status, message); err != nil {
			lastError = err
		}
	}
	return lastError
}

// ShowWaterfall shows waterfall in all GUI interfaces
func (mgui *MultiGUI) ShowWaterfall(itemName string, waterfallData []string) error {
	var lastError error
	for _, gui := range mgui.interfaces {
		if err := gui.ShowWaterfall(itemName, waterfallData); err != nil {
			lastError = err
		}
	}
	return lastError
}

// SetItemKnowledge sets knowledge in all GUI interfaces
func (mgui *MultiGUI) SetItemKnowledge(itemName string, knowledge ItemKnowledge) error {
	var lastError error
	for _, gui := range mgui.interfaces {
		if err := gui.SetItemKnowledge(itemName, knowledge); err != nil {
			lastError = err
		}
	}
	return lastError
}

// UpdateSessionSummary updates session summary in all GUI interfaces
func (mgui *MultiGUI) UpdateSessionSummary(summary SessionSummary) error {
	var lastError error
	for _, gui := range mgui.interfaces {
		if err := gui.UpdateSessionSummary(summary); err != nil {
			lastError = err
		}
	}
	return lastError
}

// GUIProgressTracker wraps ProgressTracker with GUI integration
type GUIProgressTracker struct {
	*ProgressTracker
	gui GUIInterface
}

// NewGUIProgressTracker creates a progress tracker with GUI integration
func NewGUIProgressTracker(sessionID string, logDir string, verbosity int, gui GUIInterface) *GUIProgressTracker {
	tracker := NewProgressTracker(sessionID, logDir, verbosity)
	
	guiTracker := &GUIProgressTracker{
		ProgressTracker: tracker,
		gui:             gui,
	}
	
	// Start goroutine to sync progress with GUI
	go guiTracker.syncWithGUI()
	
	return guiTracker
}

// syncWithGUI synchronizes progress updates with the GUI
func (gt *GUIProgressTracker) syncWithGUI() {
	watcher := gt.AddWatcher()
	defer gt.RemoveWatcher(watcher)
	
	for update := range watcher {
		switch update.Type {
		case "item_update":
			if status, ok := update.Data["status"].(ItemStatus); ok {
				gt.gui.UpdateStatus(update.ItemName, string(status), "")
			}
			if progress, ok := update.Data["progress"].(int); ok {
				phase := ""
				if p, ok := update.Data["phase"].(string); ok {
					phase = p
				}
				gt.gui.UpdateProgress(update.ItemName, progress, phase)
			}
			
		case "waterfall":
			if download, ok := update.Data["download"].(*DownloadProgress); ok {
				gt.gui.ShowWaterfall(update.ItemName, download.WaterfallBars)
			}
			
		case "knowledge":
			knowledge := ItemKnowledge{
				Name:             update.ItemName,
				IsInstalled:      update.Data["installed"].(bool),
				CurrentVersion:   update.Data["current_version"].(string),
				AvailableVersion: update.Data["available_version"].(string),
				NeedsUpdate:      update.Data["needs_update"].(bool),
			}
			gt.gui.SetItemKnowledge(update.ItemName, knowledge)
			
		case "session_update":
			summary := SessionSummary{
				SessionID:       gt.sessionID,
				TotalItems:      update.Data["total_items"].(int),
				CompletedItems:  update.Data["completed_items"].(int),
				CurrentItem:     update.Data["current_item"].(string),
				CurrentTime:     time.Now().Format(time.RFC3339),
			}
			if summary.TotalItems > 0 {
				summary.OverallProgress = (summary.CompletedItems * 100) / summary.TotalItems
			}
			gt.gui.UpdateSessionSummary(summary)
		}
	}
}
