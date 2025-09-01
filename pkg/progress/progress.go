// pkg/progress/progress.go - Enhanced progress tracking with waterfall display for downloads and installations

package progress

import (
	"fmt"
	"io"
	"strings"
	"sync/atomic"
	"time"

	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/utils"
)

// StatusReporter defines the minimal interface needed for status reporting
// This avoids circular dependency with pkg/status
type StatusReporter interface {
	Detail(message string)
}

// ProgressReader wraps an io.Reader to track read progress with waterfall display
type ProgressReader struct {
	reader      io.Reader
	total       int64
	read        int64
	name        string
	verbosity   int
	reporter    utils.Reporter
	lastUpdate  time.Time
	updateInterval time.Duration
}

// NewProgressReader creates a new progress tracking reader with waterfall display
func NewProgressReader(reader io.Reader, total int64, name string, verbosity int, reporter utils.Reporter) *ProgressReader {
	return &ProgressReader{
		reader:      reader,
		total:       total,
		name:        name,
		verbosity:   verbosity,
		reporter:    reporter,
		lastUpdate:  time.Now(),
		updateInterval: 500 * time.Millisecond, // Update every 500ms
	}
}

// Read implements io.Reader interface with progress tracking
func (pr *ProgressReader) Read(p []byte) (int, error) {
	n, err := pr.reader.Read(p)
	if n > 0 {
		atomic.AddInt64(&pr.read, int64(n))
		pr.updateProgress()
	}
	return n, err
}

// updateProgress handles progress display and reporting
func (pr *ProgressReader) updateProgress() {
	now := time.Now()
	if now.Sub(pr.lastUpdate) < pr.updateInterval && pr.read < pr.total {
		return // Throttle updates
	}
	pr.lastUpdate = now

	if pr.total > 0 {
		percentage := int((atomic.LoadInt64(&pr.read) * 100) / pr.total)
		
		// Update status reporter with progress
		if pr.reporter != nil {
			pr.reporter.Percent(percentage)
			
			// Format progress message
			bytesRead := atomic.LoadInt64(&pr.read)
			progressMsg := fmt.Sprintf("Downloading %s: %s / %s (%d%%)", 
				pr.name, 
				formatBytes(bytesRead), 
				formatBytes(pr.total), 
				percentage)
			pr.reporter.Detail(progressMsg)
		}

		// Show waterfall progress in verbose mode (-vvv)
		if pr.verbosity >= 3 {
			pr.displayWaterfallProgress(percentage)
		}

		// Log progress for debugging
		if pr.verbosity >= 2 {
			bytesRead := atomic.LoadInt64(&pr.read)
			logging.LogDownloadProgress(pr.name, percentage, bytesRead, pr.total)
		}
	}
}

// displayWaterfallProgress shows the cool waterfall progress display
func (pr *ProgressReader) displayWaterfallProgress(percentage int) {
	const barWidth = 50
	filled := (percentage * barWidth) / 100
	
	// Create waterfall effect with different characters
	bar := make([]rune, barWidth)
	for i := 0; i < barWidth; i++ {
		switch {
		case i < filled-3:
			bar[i] = '█' // Solid block
		case i < filled-2:
			bar[i] = '▓' // Dark shade
		case i < filled-1:
			bar[i] = '▒' // Medium shade
		case i < filled:
			bar[i] = '░' // Light shade
		default:
			bar[i] = '·' // Dot for empty
		}
	}

	// Add animation effect
	if percentage < 100 {
		// Add flowing effect at the front
		pos := filled % 4
		chars := []rune{'▌', '▌', '▊', '▊'}
		if filled < barWidth {
			bar[filled] = chars[pos]
		}
	}

	// Color coding based on progress (simplified text indicators)
	var indicator string
	switch {
	case percentage < 25:
		indicator = "[  ]" // Starting
	case percentage < 50:
		indicator = "[- ]" // Progressing
	case percentage < 75:
		indicator = "[--]" // Advancing
	case percentage < 100:
		indicator = "[->]" // Nearly done
	default:
		indicator = "[OK]" // Complete
	}

	// Format the waterfall display
	progressLine := fmt.Sprintf("%s [%s] %3d%% %s", 
		indicator,
		string(bar), 
		percentage, 
		formatBytes(atomic.LoadInt64(&pr.read)))

	// Use logging for consistent output formatting
	logging.Info("DL " + progressLine, "package", pr.name)
}

// InstallProgress tracks installation progress with emoji indicators and phase information
type InstallProgress struct {
	itemName     string
	currentPhase string
	phases       []string
	phaseIndex   int32
	completed    bool
	failed       bool
	err          error
	verbosity    int
	reporter     StatusReporter // Use our local interface instead of status.Reporter
}
type InstallProgress struct {
	name        string
	installerType string
	verbosity   int
	reporter    utils.Reporter
	startTime   time.Time
	phases      []string
	currentPhase int
}

// NewInstallProgress creates a new installation progress tracker
func NewInstallProgress(name, installerType string, verbosity int, reporter utils.Reporter) *InstallProgress {
	phases := getInstallPhases(installerType)
	
	ip := &InstallProgress{
		name:          name,
		installerType: installerType,
		verbosity:     verbosity,
		reporter:      reporter,
		startTime:     time.Now(),
		phases:        phases,
		currentPhase:  0,
	}
	
	ip.Start()
	return ip
}

// getInstallPhases returns installation phases for different installer types
func getInstallPhases(installerType string) []string {
	switch strings.ToLower(installerType) {
	case "msi":
		return []string{
			"Initializing MSI installer",
			"Extracting package contents", 
			"Validating system requirements",
			"Installing components",
			"Configuring services",
			"Finalizing installation",
		}
	case "msix":
		return []string{
			"Initializing MSIX package",
			"Validating package integrity",
			"Checking dependencies",
			"Installing application",
			"Registering package",
			"Finalizing installation",
		}
	case "exe":
		return []string{
			"Launching installer",
			"Extracting setup files",
			"Installing components",
			"Configuring application",
			"Finalizing installation",
		}
	case "powershell", "ps1":
		return []string{
			"Initializing PowerShell script",
			"Validating execution policy",
			"Running installation script",
			"Configuring components",
			"Finalizing installation",
		}
	case "nupkg":
		return []string{
			"Extracting NuGet package",
			"Validating package structure",
			"Processing dependencies",
			"Installing package contents",
			"Configuring chocolatey",
			"Finalizing installation",
		}
	default:
		return []string{
			"Initializing installer",
			"Processing package",
			"Installing components",
			"Finalizing installation",
		}
	}
}

// Start begins the installation progress tracking
func (ip *InstallProgress) Start() {
	if ip.reporter != nil {
		ip.reporter.Percent(0)
		ip.reporter.Detail(fmt.Sprintf("Starting %s installation: %s", ip.installerType, ip.name))
	}
	
	if ip.verbosity >= 2 {
		logging.LogInstallProgress(ip.name, 0, fmt.Sprintf("Starting %s installation", ip.installerType))
	}
	
	ip.displayCurrentPhase()
}

// NextPhase advances to the next installation phase
func (ip *InstallProgress) NextPhase() {
	if ip.currentPhase < len(ip.phases)-1 {
		ip.currentPhase++
		ip.displayCurrentPhase()
	}
}

// SetProgress sets a specific progress percentage
func (ip *InstallProgress) SetProgress(percentage int) {
	if ip.reporter != nil {
		ip.reporter.Percent(percentage)
	}
	
	if ip.verbosity >= 2 {
		logging.LogInstallProgress(ip.name, percentage, fmt.Sprintf("%s installation: %d%% complete", ip.installerType, percentage))
	}
}

// displayCurrentPhase shows the current installation phase
func (ip *InstallProgress) displayCurrentPhase() {
	if ip.currentPhase >= len(ip.phases) {
		return
	}
	
	phase := ip.phases[ip.currentPhase]
	percentage := ((ip.currentPhase + 1) * 100) / len(ip.phases)
	
	if ip.reporter != nil {
		ip.reporter.Detail(phase)
		ip.reporter.Percent(percentage)
	}
	
	// Show detailed progress in verbose mode
	if ip.verbosity >= 3 {
		ip.displayInstallProgress(phase, percentage)
	}
	
	if ip.verbosity >= 2 {
		logging.LogInstallProgress(ip.name, percentage, phase)
	}
}

// displayInstallProgress shows visual installation progress
func (ip *InstallProgress) displayInstallProgress(phase string, percentage int) {
	// Create a visual progress indicator for installations
	const barWidth = 40
	filled := (percentage * barWidth) / 100
	
	// Installation-specific progress bar characters
	bar := make([]rune, barWidth)
	for i := 0; i < barWidth; i++ {
		if i < filled {
			switch ip.installerType {
			case "msi":
				bar[i] = '#' // Hash for MSI
			case "msix":
				bar[i] = '=' // Equals for MSIX
			case "exe":
				bar[i] = '*' // Star for EXE
			case "powershell", "ps1":
				bar[i] = '+' // Plus for PowerShell
			case "nupkg":
				bar[i] = '@' // At for Chocolatey
			default:
				bar[i] = '●'  // Circle for other types
			}
		} else {
			bar[i] = '○' // Empty circle
		}
	}
	
	// Format the installation progress display
	elapsed := time.Since(ip.startTime)
	progressLine := fmt.Sprintf("[%s] %3d%% %s (%s elapsed)", 
		string(bar), 
		percentage, 
		phase,
		elapsed.Round(time.Second))

	// Use different emoji based on installer type
	var indicator string
	switch strings.ToLower(ip.installerType) {
	case "msi":
		indicator = "[MSI]"
	case "msix":
		indicator = "[MSIX]"
	case "exe":
		indicator = "[EXE]"
	case "powershell", "ps1":
		indicator = "[PS]"
	case "nupkg":
		indicator = "[NUPKG]"
	default:
		indicator = "[INST]"
	}

	logging.Info(fmt.Sprintf("%s %s", indicator, progressLine), "package", ip.name)
}

// Complete marks the installation as complete
func (ip *InstallProgress) Complete() {
	elapsed := time.Since(ip.startTime)
	
	if ip.reporter != nil {
		ip.reporter.Percent(100)
		ip.reporter.Detail(fmt.Sprintf("Installation completed successfully in %s", elapsed.Round(time.Second)))
	}
	
	if ip.verbosity >= 2 {
		logging.LogInstallComplete(ip.name, "", elapsed)
	}
	
	if ip.verbosity >= 3 {
		var indicator string
		switch strings.ToLower(ip.installerType) {
		case "msi":
			indicator = "[MSI]"
		case "msix":
			indicator = "[MSIX]"
		case "exe":
			indicator = "[EXE]"
		case "powershell", "ps1":
			indicator = "[PS]"
		case "nupkg":
			indicator = "[NUPKG]"
		default:
			indicator = "[DONE]"
		}
		
		logging.Info(fmt.Sprintf("%s Installation completed successfully! Time taken: %s", 
			indicator, elapsed.Round(time.Second)), "package", ip.name)
	}
}

// Fail marks the installation as failed
func (ip *InstallProgress) Fail(err error) {
	elapsed := time.Since(ip.startTime)
	
	if ip.reporter != nil {
		ip.reporter.Error(err)
	}
	
	if ip.verbosity >= 2 {
		logging.LogInstallFailed(ip.name, "", err)
	}
	
	if ip.verbosity >= 3 {
		logging.Info(fmt.Sprintf("[ERROR] Installation failed after %s: %v", 
			elapsed.Round(time.Second), err), "package", ip.name)
	}
}

// formatBytes formats byte counts in human readable format
func formatBytes(bytes int64) string {
	const unit = 1024
	if bytes < unit {
		return fmt.Sprintf("%d B", bytes)
	}
	div, exp := int64(unit), 0
	for n := bytes / unit; n >= unit; n /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.1f %cB", float64(bytes)/float64(div), "KMGTPE"[exp])
}
