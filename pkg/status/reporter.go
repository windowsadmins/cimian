// pkg/status/reporter.go - Status reporting interface and pipe implementation for CimianStatus

package status

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
	"time"

	"github.com/windowsadmins/cimian/pkg/logging"
)

// StatusMessage represents the wire format for IPC messages
type StatusMessage struct {
	Type    string `json:"type"`
	Data    string `json:"data,omitempty"`
	Percent int    `json:"percent,omitempty"`
	Error   bool   `json:"error,omitempty"`
}

// Reporter interface abstracts the status reporting functionality
type Reporter interface {
	Start(ctx context.Context) error
	Message(txt string)
	Detail(txt string)
	Percent(pct int) // -1 = indeterminate
	ShowLog(path string)
	Error(err error)
	Stop()
}

// PipeReporter implements Reporter using Windows named pipes
type PipeReporter struct {
	pipeName     string
	conn         net.Conn
	mu           sync.Mutex
	ctx          context.Context
	cancel       context.CancelFunc
	guiProcess   *os.Process
	useNamedPipe bool // If false, fall back to TCP
}

// NewPipeReporter creates a new pipe-based status reporter
func NewPipeReporter() *PipeReporter {
	return &PipeReporter{
		pipeName:     `\\.\pipe\cimianstatus`,
		useNamedPipe: true, // Try named pipe first, fall back to TCP if needed
	}
}

// Start initializes the reporter and launches cimistatus.exe
func (r *PipeReporter) Start(ctx context.Context) error {
	r.ctx, r.cancel = context.WithCancel(ctx)

	// Launch cimistatus.exe
	if err := r.launchGUI(); err != nil {
		logging.Warn("Failed to launch CimianStatus GUI, continuing without UI", "error", err)
		return nil // Don't fail completely if GUI can't start
	}

	// Wait a moment for the GUI to start and create the pipe
	time.Sleep(500 * time.Millisecond)

	// Connect to the named pipe
	if err := r.connectPipe(); err != nil {
		logging.Warn("Failed to connect to status pipe, continuing without UI", "error", err)
		return nil // Don't fail completely if pipe connection fails
	}

	logging.Info("CimianStatus reporter started successfully")
	return nil
}

// launchGUI starts the cimistatus.exe process
func (r *PipeReporter) launchGUI() error {
	// Look for cimistatus.exe in common locations
	possiblePaths := []string{
		filepath.Join(os.Getenv("ProgramFiles"), "Cimian", "cimistatus.exe"),
		filepath.Join(filepath.Dir(os.Args[0]), "cimistatus.exe"),
		"cimistatus.exe", // Assume it's in PATH
	}

	var guiPath string
	for _, path := range possiblePaths {
		if _, err := os.Stat(path); err == nil {
			guiPath = path
			break
		}
	}

	if guiPath == "" {
		return fmt.Errorf("cimistatus.exe not found in any expected location")
	}

	cmd := exec.Command(guiPath)

	// Start the process but don't wait for it
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start cimistatus.exe: %v", err)
	}

	r.guiProcess = cmd.Process
	logging.Debug("Launched CimianStatus GUI", "pid", r.guiProcess.Pid, "path", guiPath)

	// Monitor the process in a goroutine
	go func() {
		state, err := cmd.Process.Wait()
		if err != nil {
			logging.Debug("CimianStatus process wait error", "error", err)
		} else {
			logging.Debug("CimianStatus process exited", "exitCode", state.ExitCode())
		}
	}()

	return nil
}

// connectPipe establishes connection to the named pipe or TCP server
func (r *PipeReporter) connectPipe() error {
	var conn net.Conn
	var err error

	if r.useNamedPipe {
		// Try to connect to named pipe first
		for attempts := 0; attempts < 10; attempts++ {
			conn, err = dialNamedPipe(r.pipeName)
			if err == nil {
				break
			}
			time.Sleep(100 * time.Millisecond)
		}

		if err != nil {
			logging.Debug("Named pipe connection failed, falling back to TCP", "error", err)
			r.useNamedPipe = false
		}
	}

	if !r.useNamedPipe {
		// Fall back to TCP
		for attempts := 0; attempts < 10; attempts++ {
			conn, err = net.Dial("tcp", "127.0.0.1:19847")
			if err == nil {
				break
			}
			time.Sleep(100 * time.Millisecond)
		}
	}

	if err != nil {
		return fmt.Errorf("failed to connect after retries: %v", err)
	}

	r.conn = conn
	return nil
}

// dialNamedPipe connects to a Windows named pipe
func dialNamedPipe(pipeName string) (net.Conn, error) {
	// For now, we'll use TCP as a fallback since named pipes require more complex Windows API calls
	// In a future version, this would use the Windows CreateFile API to connect to the named pipe
	// For the prototype, we fall back to TCP immediately
	return net.Dial("tcp", "127.0.0.1:19847")
}

// sendMessage sends a status message over the pipe
func (r *PipeReporter) sendMessage(msg StatusMessage) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.conn == nil {
		return // Silently ignore if no connection
	}

	data, err := json.Marshal(msg)
	if err != nil {
		logging.Debug("Failed to marshal status message", "error", err)
		return
	}

	// Add newline delimiter
	data = append(data, '\n')

	if _, err := r.conn.Write(data); err != nil {
		logging.Debug("Failed to write to status pipe", "error", err)
		// Close the connection on write error
		r.conn.Close()
		r.conn = nil
	}
}

// Message sends a status message (large headline)
func (r *PipeReporter) Message(txt string) {
	logging.Info("Status", "message", txt)
	r.sendMessage(StatusMessage{
		Type: "statusMessage",
		Data: txt,
	})
}

// Detail sends a detail message (smaller, frequently changing text)
func (r *PipeReporter) Detail(txt string) {
	logging.Debug("Status detail", "detail", txt)
	r.sendMessage(StatusMessage{
		Type: "detailMessage",
		Data: txt,
	})
}

// Percent sends progress percentage (-1 for indeterminate)
func (r *PipeReporter) Percent(pct int) {
	r.sendMessage(StatusMessage{
		Type:    "percentProgress",
		Percent: pct,
	})
}

// ShowLog sends a message to show the log window
func (r *PipeReporter) ShowLog(path string) {
	r.sendMessage(StatusMessage{
		Type: "displayLog",
		Data: path,
	})
}

// Error sends an error message and sets error flag
func (r *PipeReporter) Error(err error) {
	logging.Error("Status error", "error", err)
	r.sendMessage(StatusMessage{
		Type:  "statusMessage",
		Data:  fmt.Sprintf("Error: %v", err),
		Error: true,
	})
}

// Stop closes the connection and terminates the GUI
func (r *PipeReporter) Stop() {
	if r.cancel != nil {
		r.cancel()
	}

	// Send quit message
	r.sendMessage(StatusMessage{
		Type: "quit",
	})

	// Close pipe connection
	r.mu.Lock()
	if r.conn != nil {
		r.conn.Close()
		r.conn = nil
	}
	r.mu.Unlock()

	// Give GUI time to exit gracefully
	time.Sleep(500 * time.Millisecond)

	// Force terminate if still running
	if r.guiProcess != nil {
		if err := r.guiProcess.Kill(); err != nil {
			logging.Debug("Failed to kill GUI process", "error", err)
		}
	}

	logging.Debug("CimianStatus reporter stopped")
}

// NoOpReporter implements Reporter but does nothing (for headless operation)
type NoOpReporter struct{}

func NewNoOpReporter() *NoOpReporter {
	return &NoOpReporter{}
}

func (r *NoOpReporter) Start(ctx context.Context) error { return nil }
func (r *NoOpReporter) Message(txt string)              {}
func (r *NoOpReporter) Detail(txt string)               {}
func (r *NoOpReporter) Percent(pct int)                 {}
func (r *NoOpReporter) ShowLog(path string)             {}
func (r *NoOpReporter) Error(err error)                 {}
func (r *NoOpReporter) Stop()                           {}
