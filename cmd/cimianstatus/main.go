// cmd/cimianstatus/main.go - Native Windows GUI status display for Cimian installation progress

package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"os"
	"os/exec"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"github.com/gonutz/w32"
)

// StatusMessage represents incoming messages from the CLI
type StatusMessage struct {
	Type    string `json:"type"`
	Data    string `json:"data,omitempty"`
	Percent int    `json:"percent,omitempty"`
	Error   bool   `json:"error,omitempty"`
}

// StatusState holds the current status information
type StatusState struct {
	mu            sync.RWMutex
	StatusText    string
	DetailText    string
	ProgressValue int
	ShowProgress  bool
	HasError      bool
	LogAvailable  bool
	LogPath       string
}

// CimianStatusApp holds the application state
type CimianStatusApp struct {
	tcpListener    net.Listener
	ctx            context.Context
	cancel         context.CancelFunc
	state          *StatusState
	backgroundMode bool

	// Windows GUI elements
	hwnd        w32.HWND
	statusLabel w32.HWND
	detailLabel w32.HWND
	progressBar w32.HWND
	logsButton  w32.HWND

	// Window class
	className string
}

// Windows constants
const (
	IDC_STATUS_LABEL = 1001
	IDC_DETAIL_LABEL = 1002
	IDC_PROGRESS_BAR = 1003
	IDC_LOGS_BUTTON  = 1004
)

// Window procedure for handling Windows messages
func (app *CimianStatusApp) wndProc(hwnd w32.HWND, msg uint32, wParam uintptr, lParam uintptr) uintptr {
	switch msg {
	case w32.WM_CREATE:
		app.createControls(hwnd)

	case w32.WM_COMMAND:
		switch w32.LOWORD(uint32(wParam)) {
		case IDC_LOGS_BUTTON:
			app.showLogs()
		}

	case w32.WM_CLOSE:
		app.cancel()
		w32.DestroyWindow(hwnd)

	case w32.WM_DESTROY:
		w32.PostQuitMessage(0)

	default:
		return w32.DefWindowProc(hwnd, msg, wParam, lParam)
	}
	return 0
}

// createControls creates the Windows controls (labels, progress bar, button)
func (app *CimianStatusApp) createControls(hwnd w32.HWND) {
	hInstance := w32.GetModuleHandle("")

	// Create Cimian logo/title label
	staticClass, _ := syscall.UTF16PtrFromString("STATIC")
	logoText, _ := syscall.UTF16PtrFromString("🛠️ Cimian")
	logoLabel := w32.CreateWindowEx(
		0,
		staticClass,
		logoText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER,
		20, 20, 360, 40,
		hwnd, 0, hInstance, nil)
	_ = logoLabel // Mark as used

	// Create status label
	statusText, _ := syscall.UTF16PtrFromString("Initializing...")
	app.statusLabel = w32.CreateWindowEx(
		0,
		staticClass,
		statusText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER,
		20, 80, 360, 30,
		hwnd, IDC_STATUS_LABEL, hInstance, nil)

	// Create detail label
	detailText, _ := syscall.UTF16PtrFromString("Please wait...")
	app.detailLabel = w32.CreateWindowEx(
		0,
		staticClass,
		detailText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER,
		20, 120, 360, 20,
		hwnd, IDC_DETAIL_LABEL, hInstance, nil)

	// Create progress bar
	progressClass, _ := syscall.UTF16PtrFromString("msctls_progress32")
	emptyText, _ := syscall.UTF16PtrFromString("")
	app.progressBar = w32.CreateWindowEx(
		0,
		progressClass,
		emptyText,
		w32.WS_VISIBLE|w32.WS_CHILD,
		20, 160, 360, 25,
		hwnd, IDC_PROGRESS_BAR, hInstance, nil)

	// Set progress bar range (0-100)
	w32.SendMessage(app.progressBar, 0x401, 0, uintptr(100)<<16) // PBM_SETRANGE

	// Create logs button
	buttonClass, _ := syscall.UTF16PtrFromString("BUTTON")
	buttonText, _ := syscall.UTF16PtrFromString("Show Logs")
	app.logsButton = w32.CreateWindowEx(
		0,
		buttonClass,
		buttonText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.WS_DISABLED|w32.BS_PUSHBUTTON,
		150, 200, 100, 30,
		hwnd, IDC_LOGS_BUTTON, hInstance, nil)
}

// updateUI updates the Windows controls with current state
func (app *CimianStatusApp) updateUI() {
	app.state.mu.RLock()
	defer app.state.mu.RUnlock()

	if app.hwnd == 0 {
		return
	}

	// Update status text
	w32.SetWindowText(app.statusLabel, app.state.StatusText)

	// Update detail text
	w32.SetWindowText(app.detailLabel, app.state.DetailText)

	// Update progress bar
	if app.state.ShowProgress && app.state.ProgressValue >= 0 {
		w32.SendMessage(app.progressBar, w32.PBM_SETPOS, uintptr(app.state.ProgressValue), 0)
	} else {
		// Show indeterminate progress by cycling 0-100
		pos := int(time.Now().Unix()) % 100
		w32.SendMessage(app.progressBar, w32.PBM_SETPOS, uintptr(pos), 0)
	}

	// Update logs button
	if app.state.LogAvailable {
		w32.EnableWindow(app.logsButton, true)
	} else {
		w32.EnableWindow(app.logsButton, false)
	}
}

// showLogs opens the log file
func (app *CimianStatusApp) showLogs() {
	app.state.mu.RLock()
	logPath := app.state.LogPath
	app.state.mu.RUnlock()

	if logPath == "" {
		logPath = `C:\ProgramData\ManagedInstalls\Logs\install.log`
	}
	// Try to open with notepad
	exec.Command("notepad.exe", logPath).Start()
}

func main() {
	// Create the application
	statusApp := &CimianStatusApp{
		backgroundMode: isBackgroundMode(),
		className:      "CimianStatusWindow",
		state: &StatusState{
			StatusText:   "Initializing Cimian...",
			DetailText:   "Please wait...",
			ShowProgress: false,
		},
	}

	// Initialize context
	statusApp.ctx, statusApp.cancel = context.WithCancel(context.Background())

	// Start the TCP server for CLI communication
	if err := statusApp.StartTCPServer(); err != nil {
		log.Fatalf("Failed to start TCP server: %v", err)
	}

	// Start the Windows GUI if not in background mode
	if !statusApp.backgroundMode {
		if err := statusApp.StartWindowsGUI(); err != nil {
			log.Fatalf("Failed to start Windows GUI: %v", err)
		}
	} else {
		// In background mode, just wait for context cancellation
		<-statusApp.ctx.Done()
	}

	log.Println("CimianStatus shutting down...")
}

// isBackgroundMode detects if we're running in SYSTEM context or at login screen
func isBackgroundMode() bool {
	return os.Getenv("USERNAME") == "SYSTEM" || os.Getenv("USERPROFILE") == ""
}

// StartWindowsGUI initializes and shows the Windows GUI
func (app *CimianStatusApp) StartWindowsGUI() error {
	hInstance := w32.GetModuleHandle("")

	// Register window class
	var wc w32.WNDCLASSEX
	wc.Size = uint32(unsafe.Sizeof(wc))
	wc.Style = w32.CS_HREDRAW | w32.CS_VREDRAW
	wc.WndProc = syscall.NewCallback(func(hwnd w32.HWND, msg uint32, wParam uintptr, lParam uintptr) uintptr {
		return app.wndProc(hwnd, msg, wParam, lParam)
	})
	wc.Instance = hInstance
	wc.Cursor = w32.LoadCursor(0, (*uint16)(unsafe.Pointer(uintptr(32512)))) // IDC_ARROW
	wc.Background = w32.HBRUSH(w32.COLOR_WINDOW + 1)
	className, _ := syscall.UTF16PtrFromString(app.className)
	wc.ClassName = className
	wc.Icon = w32.LoadIcon(0, (*uint16)(unsafe.Pointer(uintptr(32512))))   // IDI_APPLICATION
	wc.IconSm = w32.LoadIcon(0, (*uint16)(unsafe.Pointer(uintptr(32512)))) // IDI_APPLICATION
	if w32.RegisterClassEx(&wc) == 0 {
		return fmt.Errorf("failed to register window class")
	}
	// Create window
	classNamePtr, _ := syscall.UTF16PtrFromString(app.className)
	titlePtr, _ := syscall.UTF16PtrFromString("Cimian Status")
	app.hwnd = w32.CreateWindowEx(
		w32.WS_EX_APPWINDOW,
		classNamePtr,
		titlePtr,
		w32.WS_OVERLAPPED|w32.WS_CAPTION|w32.WS_SYSMENU|w32.WS_MINIMIZEBOX,
		w32.CW_USEDEFAULT, w32.CW_USEDEFAULT, 420, 280,
		0, 0, hInstance, nil)

	if app.hwnd == 0 {
		return fmt.Errorf("failed to create window")
	}

	// Show and update window
	w32.ShowWindow(app.hwnd, w32.SW_SHOW)
	w32.UpdateWindow(app.hwnd)

	// Start UI update timer in a goroutine
	go app.uiUpdateLoop()

	// Message loop
	var msg w32.MSG
	for {
		bRet := w32.GetMessage(&msg, 0, 0, 0)
		if bRet == 0 || bRet == -1 { // WM_QUIT or error
			break
		}
		w32.TranslateMessage(&msg)
		w32.DispatchMessage(&msg)
	}

	return nil
}

// uiUpdateLoop periodically updates the UI elements
func (app *CimianStatusApp) uiUpdateLoop() {
	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()

	for {
		select {
		case <-app.ctx.Done():
			return
		case <-ticker.C:
			app.updateUI()
		}
	}
} // StartTCPServer starts the TCP server for CLI communication
func (app *CimianStatusApp) StartTCPServer() error {
	listener, err := net.Listen("tcp", "127.0.0.1:19847")
	if err != nil {
		return fmt.Errorf("failed to create TCP listener: %v", err)
	}

	app.tcpListener = listener
	go app.handleTCPConnections()

	log.Printf("CimianStatus TCP server listening on %s", listener.Addr().String())
	return nil
}

// handleTCPConnections accepts and processes TCP connections from CLI
func (app *CimianStatusApp) handleTCPConnections() {
	for {
		select {
		case <-app.ctx.Done():
			return
		default:
			conn, err := app.tcpListener.Accept()
			if err != nil {
				select {
				case <-app.ctx.Done():
					return
				default:
					log.Printf("TCP accept error: %v", err)
					continue
				}
			}

			go app.handleConnection(conn)
		}
	}
}

// handleConnection processes a single TCP connection
func (app *CimianStatusApp) handleConnection(conn net.Conn) {
	defer conn.Close()

	scanner := bufio.NewScanner(conn)
	for scanner.Scan() {
		line := scanner.Text()
		if strings.TrimSpace(line) == "" {
			continue
		}

		var msg StatusMessage
		if err := json.Unmarshal([]byte(line), &msg); err != nil {
			log.Printf("Failed to unmarshal message: %v", err)
			continue
		}

		app.processMessage(msg)
	}

	if err := scanner.Err(); err != nil {
		log.Printf("Scanner error: %v", err)
	}
}

// processMessage handles incoming status messages and updates the UI
func (app *CimianStatusApp) processMessage(msg StatusMessage) {
	app.state.mu.Lock()
	defer app.state.mu.Unlock()

	switch msg.Type {
	case "statusMessage":
		app.state.StatusText = msg.Data
		app.state.HasError = msg.Error
		log.Printf("Status: %s", msg.Data)

	case "detailMessage":
		app.state.DetailText = msg.Data
		log.Printf("Detail: %s", msg.Data)

	case "percentProgress":
		if msg.Percent >= 0 {
			app.state.ProgressValue = msg.Percent
			app.state.ShowProgress = true
		} else {
			app.state.ShowProgress = false
		}
		log.Printf("Progress: %d%%", msg.Percent)

	case "displayLog":
		app.state.LogPath = msg.Data
		app.state.LogAvailable = true

	case "quit":
		log.Println("Received quit message")
		app.cancel()
		if app.hwnd != 0 {
			w32.PostMessage(app.hwnd, w32.WM_CLOSE, 0, 0)
		}
		return
	}
}
