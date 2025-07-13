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
	"path/filepath"
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
	LastRunTime   string // Store the last successful run time
}

// CimianStatusApp holds the application state
type CimianStatusApp struct {
	tcpListener    net.Listener
	ctx            context.Context
	cancel         context.CancelFunc
	state          *StatusState
	backgroundMode bool

	// Windows GUI elements
	hwnd          w32.HWND
	statusLabel   w32.HWND
	detailLabel   w32.HWND
	scheduleLabel w32.HWND
	progressBar   w32.HWND
	logsButton    w32.HWND
	runNowButton  w32.HWND

	// Window class
	className string
}

// Windows constants
const (
	IDC_STATUS_LABEL   = 1001
	IDC_DETAIL_LABEL   = 1002
	IDC_SCHEDULE_LABEL = 1003
	IDC_PROGRESS_BAR   = 1004
	IDC_LOGS_BUTTON    = 1005
	IDC_RUN_NOW_BUTTON = 1006
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
		case IDC_RUN_NOW_BUTTON:
			app.runNow()
		}

	case w32.WM_CTLCOLORSTATIC:
		// Make static controls completely transparent and blend with window background
		gdi32 := syscall.NewLazyDLL("gdi32.dll")
		setTextColor := gdi32.NewProc("SetTextColor")
		setBkMode := gdi32.NewProc("SetBkMode")

		// Set text color to black
		setTextColor.Call(wParam, uintptr(0x00000000)) // Black text
		// Set background to transparent mode
		setBkMode.Call(wParam, uintptr(1)) // TRANSPARENT mode

		// Return a null brush so the parent window background shows through
		return uintptr(w32.GetStockObject(w32.NULL_BRUSH))

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

	// Enable DPI awareness for better display quality
	// Note: Temporarily commented out for ARM64 compatibility testing
	// user32 := syscall.NewLazyDLL("user32.dll")
	// setProcessDpiAwareness := user32.NewProc("SetProcessDpiAwarenessW")
	// setProcessDpiAwareness.Call(uintptr(1)) // PROCESS_DPI_AWARENESS_SYSTEM_AWARE

	// Create Aptos font using Windows API - 18pt size
	gdi32 := syscall.NewLazyDLL("gdi32.dll")
	createFontW := gdi32.NewProc("CreateFontW")

	aptosName, _ := syscall.UTF16PtrFromString("Aptos")
	aptosFont, _, _ := createFontW.Call(
		uintptr(18),                        // Height (14pt = ~18 pixels)
		uintptr(0),                         // Width
		uintptr(0),                         // Escapement
		uintptr(0),                         // Orientation
		uintptr(400),                       // Weight (FW_NORMAL)
		uintptr(0),                         // Italic
		uintptr(0),                         // Underline
		uintptr(0),                         // StrikeOut
		uintptr(1),                         // CharSet (DEFAULT_CHARSET)
		uintptr(0),                         // OutPrecision
		uintptr(0),                         // ClipPrecision
		uintptr(5),                         // Quality (CLEARTYPE_QUALITY)
		uintptr(0),                         // PitchAndFamily
		uintptr(unsafe.Pointer(aptosName))) // Font name

	// Load and display the Cimian logo image
	logoPath := filepath.Join(filepath.Dir(os.Args[0]), "..", "..", "cimian.png")
	if _, err := os.Stat(logoPath); os.IsNotExist(err) {
		// Fallback to current directory
		logoPath = "cimian.png"
		if _, err := os.Stat(logoPath); os.IsNotExist(err) {
			// Try relative path from executable
			logoPath = filepath.Join(filepath.Dir(os.Args[0]), "cimian.png")
		}
	}

	// Create a static control to display the logo
	staticClass, _ := syscall.UTF16PtrFromString("STATIC")
	logoText, _ := syscall.UTF16PtrFromString("Cimian")
	logoLabel := w32.CreateWindowEx(
		0,
		staticClass,
		logoText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER|w32.SS_BITMAP,
		146, 20, 128, 128, // Center horizontally: (420-128)/2 = 146
		hwnd, 0, hInstance, nil)

	// Enhanced logo loading with debug information
	fmt.Printf("Attempting to load logo from: %s\n", logoPath)
	if _, err := os.Stat(logoPath); err != nil {
		fmt.Printf("Logo file not found: %v\n", err)
		// Remove the SS_BITMAP style and show text instead
		w32.SetWindowLong(logoLabel, w32.GWL_STYLE,
			w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER)
		w32.SetWindowText(logoLabel, "Cimian")
	} else {
		fmt.Printf("Logo file found, loading...\n")

		// Initialize GDI+
		gdiplus := syscall.NewLazyDLL("gdiplus.dll")
		gdiplusStartup := gdiplus.NewProc("GdiplusStartup")
		gdipCreateBitmapFromFile := gdiplus.NewProc("GdipCreateBitmapFromFile")
		gdipCreateHBITMAPFromBitmap := gdiplus.NewProc("GdipCreateHBITMAPFromBitmap")
		gdipCreateBitmapFromScan0 := gdiplus.NewProc("GdipCreateBitmapFromScan0")
		gdipGetImageGraphicsContext := gdiplus.NewProc("GdipGetImageGraphicsContext")
		gdipDrawImageRectI := gdiplus.NewProc("GdipDrawImageRectI")
		gdipDeleteGraphics := gdiplus.NewProc("GdipDeleteGraphics")
		gdipDisposeImage := gdiplus.NewProc("GdipDisposeImage")
		gdiplusShutdown := gdiplus.NewProc("GdiplusShutdown")

		var gdiplusToken uintptr
		startupInput := [3]uintptr{1, 0, 0}
		r1, _, _ := gdiplusStartup.Call(uintptr(unsafe.Pointer(&gdiplusToken)), uintptr(unsafe.Pointer(&startupInput)), 0)

		if r1 == 0 {
			fmt.Printf("GDI+ started successfully\n")
			logoPathW, _ := syscall.UTF16PtrFromString(logoPath)
			var gpBitmap uintptr
			result1, _, _ := gdipCreateBitmapFromFile.Call(uintptr(unsafe.Pointer(logoPathW)), uintptr(unsafe.Pointer(&gpBitmap)))

			if result1 == 0 && gpBitmap != 0 {
				fmt.Printf("Bitmap loaded successfully\n")

				// Create a 128x128 bitmap for scaling
				var scaledBitmap uintptr
				r2, _, _ := gdipCreateBitmapFromScan0.Call(128, 128, 0, 0x26200A, 0, uintptr(unsafe.Pointer(&scaledBitmap)))
				if r2 == 0 {
					fmt.Printf("Scaled bitmap created successfully\n")
					var graphics uintptr
					r3, _, _ := gdipGetImageGraphicsContext.Call(scaledBitmap, uintptr(unsafe.Pointer(&graphics)))
					if r3 == 0 {
						fmt.Printf("Graphics context created successfully\n")
						// Draw the original image scaled to 128x128
						gdipDrawImageRectI.Call(graphics, gpBitmap, 0, 0, 128, 128)

						// Convert to HBITMAP with white background
						var hBitmap uintptr
						backgroundColor := uintptr(0xFFFFFFFF) // White background
						r4, _, _ := gdipCreateHBITMAPFromBitmap.Call(scaledBitmap, uintptr(unsafe.Pointer(&hBitmap)), backgroundColor)
						if r4 == 0 && hBitmap != 0 {
							fmt.Printf("Logo bitmap created successfully, setting to control\n")
							// Set the bitmap to the static control
							w32.SendMessage(logoLabel, 0x172, 0, hBitmap) // STM_SETIMAGE with IMAGE_BITMAP (0)
						} else {
							fmt.Printf("Failed to create HBITMAP from bitmap, error: %d\n", r4)
						}
						gdipDeleteGraphics.Call(graphics)
					} else {
						fmt.Printf("Failed to get graphics context, error: %d\n", r3)
					}
					gdipDisposeImage.Call(scaledBitmap)
				} else {
					fmt.Printf("Failed to create scaled bitmap, error: %d\n", r2)
				}
				gdipDisposeImage.Call(gpBitmap)
			} else {
				fmt.Printf("Failed to create bitmap from file, error: %d\n", result1)
			}
			gdiplusShutdown.Call(gdiplusToken)
		} else {
			fmt.Printf("Failed to start GDI+, error: %d\n", r1)
		}
	}

	// Create status label (dynamic content)
	statusText, _ := syscall.UTF16PtrFromString("Cimian Status Ready")
	app.statusLabel = w32.CreateWindowEx(
		0,
		staticClass,
		statusText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER,
		20, 160, 360, 30, // Moved down to accommodate logo
		hwnd, IDC_STATUS_LABEL, hInstance, nil)
	w32.SendMessage(app.statusLabel, w32.WM_SETFONT, aptosFont, 1)

	// Create detail label (split into two lines)
	detailText, _ := syscall.UTF16PtrFromString("Last run: Never")
	app.detailLabel = w32.CreateWindowEx(
		0,
		staticClass,
		detailText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER,
		20, 200, 360, 20, // Moved down to accommodate logo
		hwnd, IDC_DETAIL_LABEL, hInstance, nil)
	w32.SendMessage(app.detailLabel, w32.WM_SETFONT, aptosFont, 1)

	// Create schedule label (new line for next scheduled run)
	scheduleText, _ := syscall.UTF16PtrFromString("Next scheduled run in 1 hour")
	app.scheduleLabel = w32.CreateWindowEx(
		0,
		staticClass,
		scheduleText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.SS_CENTER,
		20, 220, 360, 20, // New line for schedule info
		hwnd, IDC_SCHEDULE_LABEL, hInstance, nil)
	w32.SendMessage(app.scheduleLabel, w32.WM_SETFONT, aptosFont, 1)

	// Create progress bar with modern style
	progressClass, _ := syscall.UTF16PtrFromString("msctls_progress32")
	emptyTextProgress, _ := syscall.UTF16PtrFromString("")
	app.progressBar = w32.CreateWindowEx(
		w32.WS_EX_CLIENTEDGE, // Add border for better visual
		progressClass,
		emptyTextProgress,
		w32.WS_VISIBLE|w32.WS_CHILD|0x01, // Add PBS_SMOOTH style
		20, 250, 360, 25,                 // Moved down to accommodate schedule label
		hwnd, IDC_PROGRESS_BAR, hInstance, nil)

	// Set progress bar range (0-100) and enable modern style
	w32.SendMessage(app.progressBar, 0x401, 0, uintptr(100)<<16) // PBM_SETRANGE
	w32.SendMessage(app.progressBar, 0x410, 0, 0x01)             // PBM_SETBARCOLOR - use system color
	w32.SendMessage(app.progressBar, 0x40D, 0, 0)                // PBM_SETBKCOLOR - default background

	// Create logs button
	buttonClass, _ := syscall.UTF16PtrFromString("BUTTON")
	buttonText, _ := syscall.UTF16PtrFromString("Show Logs")
	app.logsButton = w32.CreateWindowEx(
		0,
		buttonClass,
		buttonText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.WS_DISABLED|w32.BS_PUSHBUTTON,
		80, 290, 100, 30, // Moved down to accommodate schedule label
		hwnd, IDC_LOGS_BUTTON, hInstance, nil)
	w32.SendMessage(app.logsButton, w32.WM_SETFONT, aptosFont, 1)

	// Create run now button
	runNowText, _ := syscall.UTF16PtrFromString("Run Now")
	app.runNowButton = w32.CreateWindowEx(
		0,
		buttonClass,
		runNowText,
		w32.WS_VISIBLE|w32.WS_CHILD|w32.BS_PUSHBUTTON,
		220, 290, 100, 30, // Moved down to accommodate schedule label
		hwnd, IDC_RUN_NOW_BUTTON, hInstance, nil)
	w32.SendMessage(app.runNowButton, w32.WM_SETFONT, aptosFont, 1)
}

// updateUI updates the Windows controls with current state
func (app *CimianStatusApp) updateUI() {
	app.state.mu.RLock()
	defer app.state.mu.RUnlock()

	if app.hwnd == 0 {
		return
	}

	// Update status text (dynamic)
	w32.SetWindowText(app.statusLabel, app.state.StatusText)

	// Update detail text (last run time)
	if app.state.LastRunTime != "" && app.state.LastRunTime != "Never" {
		w32.SetWindowText(app.detailLabel, fmt.Sprintf("Last run: %s", app.state.LastRunTime))
	} else {
		w32.SetWindowText(app.detailLabel, "Last run: Never")
	}

	// Update schedule text (static for now)
	w32.SetWindowText(app.scheduleLabel, "Next scheduled run in 1 hour")

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

	// Change window title based on error state
	if app.state.HasError {
		w32.SetWindowText(app.hwnd, "Cimian Status - Error")
	} else {
		w32.SetWindowText(app.hwnd, "Cimian Status")
	}
}

// showLogs opens the log file
func (app *CimianStatusApp) showLogs() {
	app.state.mu.RLock()
	logPath := app.state.LogPath
	app.state.mu.RUnlock()

	if logPath == "" {
		// Find the latest timestamped log directory
		logsBaseDir := `C:\ProgramData\ManagedInstalls\logs`
		entries, err := os.ReadDir(logsBaseDir)
		if err == nil {
			var latestDir string
			for _, entry := range entries {
				if entry.IsDir() && len(entry.Name()) == 15 { // YYYY-MM-DD-HHmmss format
					if latestDir == "" || entry.Name() > latestDir {
						latestDir = entry.Name()
					}
				}
			}
			if latestDir != "" {
				logPath = filepath.Join(logsBaseDir, latestDir, "install.log")
			} else {
				logPath = `C:\ProgramData\ManagedInstalls\logs\install.log` // fallback
			}
		} else {
			logPath = `C:\ProgramData\ManagedInstalls\logs\install.log` // fallback
		}
	}
	// Try to open with notepad
	exec.Command("notepad.exe", logPath).Start()
}

// runNow executes the managedsoftwareupdate.exe process
func (app *CimianStatusApp) runNow() {
	// Disable the run now button during execution
	w32.EnableWindow(app.runNowButton, false)
	w32.SetWindowText(app.runNowButton, "Running...")

	// Update status
	app.state.mu.Lock()
	app.state.StatusText = "Running Cimian update check..."
	app.state.DetailText = "Please wait..."
	app.state.ShowProgress = true
	app.state.ProgressValue = 0
	app.state.HasError = false
	app.state.mu.Unlock()

	// Run the update in a goroutine
	go app.executeUpdate()
}

// executeUpdate runs the managedsoftwareupdate.exe process
func (app *CimianStatusApp) executeUpdate() {
	defer func() {
		// Re-enable the run now button
		w32.EnableWindow(app.runNowButton, true)
		w32.SetWindowText(app.runNowButton, "Run Now")
	}()

	// Find the managedsoftwareupdate.exe executable
	execPath, err := app.findExecutable()
	if err != nil {
		app.state.mu.Lock()
		app.state.StatusText = "Error: Could not find managedsoftwareupdate.exe"
		app.state.DetailText = err.Error()
		app.state.HasError = true
		app.state.ShowProgress = false
		app.state.mu.Unlock()
		log.Printf("Error finding executable: %v", err)
		return
	}

	// Update status
	app.state.mu.Lock()
	app.state.StatusText = "Running Cimian update..."
	app.state.DetailText = "Checking for updates..."
	app.state.ProgressValue = 25
	app.state.mu.Unlock()

	// Execute the command with PowerShell to bypass unsigned binary restrictions
	// Use PowerShell's -ExecutionPolicy Bypass to run unsigned binaries
	cmd := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-Command",
		fmt.Sprintf("& '%s' --show-status", execPath))

	// Start the process
	if err := cmd.Start(); err != nil {
		app.state.mu.Lock()
		app.state.StatusText = "Error: Failed to start update process"
		app.state.DetailText = fmt.Sprintf("Cannot execute unsigned binary: %v", err)
		app.state.HasError = true
		app.state.ShowProgress = false
		app.state.mu.Unlock()
		log.Printf("Error starting process: %v", err)
		return
	}

	// Wait for the process to complete
	err = cmd.Wait()

	// Update final status
	app.state.mu.Lock()
	if err != nil {
		app.state.StatusText = "Update completed with errors"
		app.state.DetailText = err.Error()
		app.state.HasError = true
		app.state.ShowProgress = false
		log.Printf("Update process completed with error: %v", err)
	} else {
		app.state.StatusText = "Update completed successfully"
		app.state.HasError = false
		app.state.ShowProgress = true
		app.state.ProgressValue = 100

		// Update last run time
		updateLastRunTime()
		app.state.LastRunTime = getLastRunTime()
		app.state.DetailText = fmt.Sprintf("Last successful run: %s", app.state.LastRunTime)

		log.Printf("Update process completed successfully")
	}
	app.state.mu.Unlock()
}

// findExecutable locates the managedsoftwareupdate.exe executable
func (app *CimianStatusApp) findExecutable() (string, error) {
	// Look for managedsoftwareupdate.exe in common locations
	possiblePaths := []string{
		filepath.Join(os.Getenv("ProgramFiles"), "Cimian", "managedsoftwareupdate.exe"),
		filepath.Join(filepath.Dir(os.Args[0]), "managedsoftwareupdate.exe"),
		"managedsoftwareupdate.exe", // Assume it's in PATH
	}

	for _, path := range possiblePaths {
		if _, err := os.Stat(path); err == nil {
			return path, nil
		}
	}

	return "", fmt.Errorf("managedsoftwareupdate.exe not found in any expected location")
}

func main() {
	// Create the application
	statusApp := &CimianStatusApp{
		backgroundMode: isBackgroundMode(),
		className:      "CimianStatusWindow",
		state: &StatusState{
			StatusText:   "Cimian Status Ready",
			ShowProgress: false,
			LastRunTime:  getLastRunTime(),
		},
	}

	// Set initial detail text with last run time
	if statusApp.state.LastRunTime == "Never" {
		statusApp.state.DetailText = "Last run: Never"
	} else {
		statusApp.state.DetailText = fmt.Sprintf("Last run: %s", statusApp.state.LastRunTime)
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

// getLastRunTime reads the last successful run time from file
func getLastRunTime() string {
	lastRunFile := filepath.Join(os.Getenv("ProgramData"), "ManagedInstalls", "LastRunTime.txt")
	if data, err := os.ReadFile(lastRunFile); err == nil {
		return strings.TrimSpace(string(data))
	}
	return "Never"
}

// updateLastRunTime saves the current time as the last successful run time
func updateLastRunTime() {
	currentTime := time.Now().Format("2006-01-02 15:04:05")
	lastRunFile := filepath.Join(os.Getenv("ProgramData"), "ManagedInstalls", "LastRunTime.txt")
	os.MkdirAll(filepath.Dir(lastRunFile), 0755)
	os.WriteFile(lastRunFile, []byte(currentTime), 0644)
}

// StartWindowsGUI initializes and shows the Windows GUI
func (app *CimianStatusApp) StartWindowsGUI() error {
	hInstance := w32.GetModuleHandle("")

	// Register window class with icon loading
	var wc w32.WNDCLASSEX
	wc.Size = uint32(unsafe.Sizeof(wc))
	wc.Style = w32.CS_HREDRAW | w32.CS_VREDRAW
	wc.WndProc = syscall.NewCallback(func(hwnd w32.HWND, msg uint32, wParam uintptr, lParam uintptr) uintptr {
		return app.wndProc(hwnd, msg, wParam, lParam)
	})
	wc.Instance = hInstance
	wc.Cursor = w32.LoadCursor(0, w32.MakeIntResource(w32.IDC_ARROW))
	wc.Background = w32.HBRUSH(w32.COLOR_BTNFACE + 1) // Use standard dialog background
	className, _ := syscall.UTF16PtrFromString(app.className)
	wc.ClassName = className

	// Try to load custom icon from icon.png
	iconPath := filepath.Join(filepath.Dir(os.Args[0]), "..", "..", "icon.png")
	if _, err := os.Stat(iconPath); os.IsNotExist(err) {
		iconPath = "icon.png"
	}

	customIcon := w32.HICON(0)
	if _, err := os.Stat(iconPath); err == nil {
		// Load PNG icon using GDI+ and convert to HICON
		gdiplus := syscall.NewLazyDLL("gdiplus.dll")
		if gdiplus.Name != "" {
			gdiplusStartup := gdiplus.NewProc("GdiplusStartup")
			gdipCreateBitmapFromFile := gdiplus.NewProc("GdipCreateBitmapFromFile")
			gdipCreateHICONFromBitmap := gdiplus.NewProc("GdipCreateHICONFromBitmap")
			gdipDisposeImage := gdiplus.NewProc("GdipDisposeImage")
			gdiplusShutdown := gdiplus.NewProc("GdiplusShutdown")

			var gdiplusToken uintptr
			startupInput := [3]uintptr{1, 0, 0}
			r1, _, _ := gdiplusStartup.Call(uintptr(unsafe.Pointer(&gdiplusToken)), uintptr(unsafe.Pointer(&startupInput)), 0)
			if r1 == 0 {
				iconPathW, _ := syscall.UTF16PtrFromString(iconPath)
				var gpBitmap uintptr
				result1, _, _ := gdipCreateBitmapFromFile.Call(uintptr(unsafe.Pointer(iconPathW)), uintptr(unsafe.Pointer(&gpBitmap)))
				if result1 == 0 && gpBitmap != 0 {
					var hIcon uintptr
					r2, _, _ := gdipCreateHICONFromBitmap.Call(gpBitmap, uintptr(unsafe.Pointer(&hIcon)))
					if r2 == 0 {
						customIcon = w32.HICON(hIcon)
					}
					gdipDisposeImage.Call(gpBitmap)
				}
				gdiplusShutdown.Call(gdiplusToken)
			}
		}
	}

	// Set icon - use custom icon if loaded, otherwise default
	if customIcon != 0 {
		wc.Icon = customIcon
		wc.IconSm = customIcon
	} else {
		wc.Icon = w32.LoadIcon(0, w32.MakeIntResource(w32.IDI_APPLICATION))
		wc.IconSm = w32.LoadIcon(0, w32.MakeIntResource(w32.IDI_APPLICATION))
	}

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
		w32.CW_USEDEFAULT, w32.CW_USEDEFAULT, 420, 390, // Increased height for schedule label
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
