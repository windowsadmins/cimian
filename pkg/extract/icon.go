// pkg/extract/icon.go - functions for extracting icons from installers

//go:build windows
// +build windows

package extract

import (
	"fmt"
	"image"
	"image/color"
	"image/png"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"

	"golang.org/x/image/draw"
)

var (
	user32                     = syscall.NewLazyDLL("user32.dll")
	shell32                    = syscall.NewLazyDLL("shell32.dll")
	procLoadImageW             = user32.NewProc("LoadImageW")
	procDestroyIcon            = user32.NewProc("DestroyIcon")
	procExtractIconExW         = shell32.NewProc("ExtractIconExW")
	procGetIconInfo            = user32.NewProc("GetIconInfo")
	procGetDIBits              = syscall.NewLazyDLL("gdi32.dll").NewProc("GetDIBits")
	procDeleteObject           = syscall.NewLazyDLL("gdi32.dll").NewProc("DeleteObject")
	procCreateCompatibleDC     = syscall.NewLazyDLL("gdi32.dll").NewProc("CreateCompatibleDC")
	procDeleteDC               = syscall.NewLazyDLL("gdi32.dll").NewProc("DeleteDC")
	procSelectObject           = syscall.NewLazyDLL("gdi32.dll").NewProc("SelectObject")
)

const (
	IMAGE_ICON        = 1
	LR_DEFAULTSIZE    = 0x00000040
	LR_LOADFROMFILE   = 0x00000010
	BI_RGB            = 0
)

type ICONINFO struct {
	fIcon    uint32
	xHotspot uint32
	yHotspot uint32
	hbmMask  uintptr
	hbmColor uintptr
}

type BITMAPINFOHEADER struct {
	biSize          uint32
	biWidth         int32
	biHeight        int32
	biPlanes        uint16
	biBitCount      uint16
	biCompression   uint32
	biSizeImage     uint32
	biXPelsPerMeter int32
	biYPelsPerMeter int32
	biClrUsed       uint32
	biClrImportant  uint32
}

// IconExtractResult represents the result of icon extraction
type IconExtractResult struct {
	Success     bool
	IconPath    string
	Error       error
	Method      string
}

// ExtractIconFromInstaller extracts an icon from an installer file
// Returns the path to the extracted icon (PNG format)
func ExtractIconFromInstaller(installerPath, outputPath string) (*IconExtractResult, error) {
	ext := strings.ToLower(filepath.Ext(installerPath))
	
	result := &IconExtractResult{
		Success: false,
	}

	switch ext {
	case ".exe":
		result = extractIconFromExe(installerPath, outputPath)
	case ".msi":
		result = extractIconFromMsi(installerPath, outputPath)
	case ".msix", ".appx":
		result = extractIconFromMsix(installerPath, outputPath)
	case ".nupkg":
		result = extractIconFromNupkg(installerPath, outputPath)
	default:
		result.Error = fmt.Errorf("unsupported installer type: %s", ext)
		return result, result.Error
	}

	return result, result.Error
}

// extractIconFromExe extracts icon from an EXE file using PowerShell
func extractIconFromExe(exePath, outputPath string) *IconExtractResult {
	result := &IconExtractResult{
		Method: "exe-powershell",
	}

	// Use PowerShell to extract and convert the icon
	// This is more reliable than the direct API approach
	psScript := fmt.Sprintf(`
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

try {
    # Extract icon from EXE
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon('%s')
    if ($null -eq $icon) {
        Write-Error "No icon found in EXE"
        exit 1
    }
    
    # Convert to bitmap
    $bitmap = $icon.ToBitmap()
    
    # Resize to 512x512 if needed
    $targetSize = 512
    if ($bitmap.Width -ne $targetSize -or $bitmap.Height -ne $targetSize) {
        $newBitmap = New-Object System.Drawing.Bitmap($targetSize, $targetSize)
        $graphics = [System.Drawing.Graphics]::FromImage($newBitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($bitmap, 0, 0, $targetSize, $targetSize)
        $graphics.Dispose()
        $bitmap.Dispose()
        $bitmap = $newBitmap
    }
    
    # Save as PNG
    $bitmap.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $icon.Dispose()
    
    Write-Output "Success"
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
`, exePath, outputPath)

	out, err := exec.Command("powershell", "-NoProfile", "-Command", psScript).CombinedOutput()
	if err != nil {
		result.Error = fmt.Errorf("PowerShell icon extraction failed: %w, output: %s", err, string(out))
		return result
	}

	if !fileExists(outputPath) {
		result.Error = fmt.Errorf("icon extraction did not produce output file")
		return result
	}

	result.Success = true
	result.IconPath = outputPath
	return result
}

// extractIconFromMsi extracts icon from an MSI file
func extractIconFromMsi(msiPath, outputPath string) *IconExtractResult {
	result := &IconExtractResult{
		Method: "msi-powershell",
	}

	// Use PowerShell to extract icon information from MSI
	psScript := fmt.Sprintf(`
$ErrorActionPreference = 'Stop'
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @('%s', 0))
    
    # Query for ARPPRODUCTICON property
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, "SELECT Value FROM Property WHERE Property='ARPPRODUCTICON'")
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    
    if ($record) {
        $iconName = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
        Write-Output $iconName
    } else {
        Write-Output ""
    }
} catch {
    Write-Output ""
}
`, msiPath)

	out, err := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", psScript).Output()
	if err != nil || len(out) == 0 {
		result.Error = fmt.Errorf("failed to query MSI icon property")
		return result
	}

	iconName := strings.TrimSpace(string(out))
	if iconName == "" {
		result.Error = fmt.Errorf("no icon property found in MSI")
		return result
	}

	// Extract the actual icon binary from the Icon table
	psExtractScript := fmt.Sprintf(`
$ErrorActionPreference = 'Stop'
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @('%s', 0))
    
    # Query Icon table
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, "SELECT Data FROM Icon WHERE Name='%s'")
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    
    if ($record) {
        $tempIcon = Join-Path $env:TEMP "extracted_icon.ico"
        $record.GetType().InvokeMember('ReadStream', 'InvokeMethod', $null, $record, @(1, 999999, $tempIcon))
        Write-Output $tempIcon
    }
} catch {
    Write-Error $_.Exception.Message
}
`, msiPath, iconName)

	out, err = exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", psExtractScript).CombinedOutput()
	if err != nil {
		result.Error = fmt.Errorf("failed to extract icon from MSI: %w, output: %s", err, string(out))
		return result
	}

	tempIconPath := strings.TrimSpace(string(out))
	if tempIconPath == "" || !fileExists(tempIconPath) {
		result.Error = fmt.Errorf("icon extraction produced no file")
		return result
	}
	defer os.Remove(tempIconPath)

	// Convert .ico to .png
	if err := convertIcoToPng(tempIconPath, outputPath); err != nil {
		result.Error = fmt.Errorf("failed to convert icon to PNG: %w", err)
		return result
	}

	result.Success = true
	result.IconPath = outputPath
	return result
}

// extractIconFromMsix extracts icon from MSIX/AppX package
func extractIconFromMsix(msixPath, outputPath string) *IconExtractResult {
	result := &IconExtractResult{
		Method: "msix-manifest",
	}

	// MSIX files are ZIP archives - extract manifest and find logo
	tempDir := filepath.Join(os.TempDir(), fmt.Sprintf("msix_extract_%d", os.Getpid()))
	defer os.RemoveAll(tempDir)

	// Extract the MSIX package
	if err := exec.Command("powershell", "-Command", 
		fmt.Sprintf("Expand-Archive -Path '%s' -DestinationPath '%s' -Force", msixPath, tempDir)).Run(); err != nil {
		result.Error = fmt.Errorf("failed to extract MSIX: %w", err)
		return result
	}

	// Parse AppxManifest.xml to find logo path
	manifestPath := filepath.Join(tempDir, "AppxManifest.xml")
	logoPath, err := parseAppxManifestForLogo(manifestPath)
	if err != nil {
		result.Error = fmt.Errorf("failed to parse manifest: %w", err)
		return result
	}

	// Find the logo file
	fullLogoPath := filepath.Join(tempDir, logoPath)
	
	// Look for scale variants (e.g., logo.scale-200.png)
	logoDir := filepath.Dir(fullLogoPath)
	logoBase := strings.TrimSuffix(filepath.Base(fullLogoPath), filepath.Ext(fullLogoPath))
	
	// Try to find the highest resolution variant
	bestLogoPath := findBestLogoVariant(logoDir, logoBase)
	if bestLogoPath == "" {
		bestLogoPath = fullLogoPath
	}

	if !fileExists(bestLogoPath) {
		result.Error = fmt.Errorf("logo file not found: %s", bestLogoPath)
		return result
	}

	// Copy and convert if needed
	if strings.ToLower(filepath.Ext(bestLogoPath)) == ".png" {
		// Already PNG, just copy
		if err := copyFile(bestLogoPath, outputPath); err != nil {
			result.Error = fmt.Errorf("failed to copy logo: %w", err)
			return result
		}
	} else {
		// Convert to PNG
		if err := convertImageToPng(bestLogoPath, outputPath); err != nil {
			result.Error = fmt.Errorf("failed to convert logo to PNG: %w", err)
			return result
		}
	}

	result.Success = true
	result.IconPath = outputPath
	return result
}

// extractIconFromNupkg extracts icon from NuGet package
func extractIconFromNupkg(nupkgPath, outputPath string) *IconExtractResult {
	result := &IconExtractResult{
		Method: "nupkg-nuspec",
	}

	// NuGet packages are ZIP archives
	tempDir := filepath.Join(os.TempDir(), fmt.Sprintf("nupkg_extract_%d", os.Getpid()))
	defer os.RemoveAll(tempDir)

	// Extract the package
	if err := exec.Command("powershell", "-Command",
		fmt.Sprintf("Expand-Archive -Path '%s' -DestinationPath '%s' -Force", nupkgPath, tempDir)).Run(); err != nil {
		result.Error = fmt.Errorf("failed to extract nupkg: %w", err)
		return result
	}

	// Look for icon file - check common locations
	iconPaths := []string{
		filepath.Join(tempDir, "icon.png"),
		filepath.Join(tempDir, "icon.jpg"),
		filepath.Join(tempDir, "logo.png"),
		filepath.Join(tempDir, "logo.jpg"),
	}

	var foundIcon string
	for _, iconPath := range iconPaths {
		if fileExists(iconPath) {
			foundIcon = iconPath
			break
		}
	}

	// If no direct icon, check nuspec for icon reference
	if foundIcon == "" {
		nuspecFiles, _ := filepath.Glob(filepath.Join(tempDir, "*.nuspec"))
		if len(nuspecFiles) > 0 {
			iconRef := parseNuspecForIcon(nuspecFiles[0])
			if iconRef != "" {
				potentialIcon := filepath.Join(tempDir, iconRef)
				if fileExists(potentialIcon) {
					foundIcon = potentialIcon
				}
			}
		}
	}

	if foundIcon == "" {
		result.Error = fmt.Errorf("no icon found in nupkg")
		return result
	}

	// Convert to PNG if needed
	if strings.ToLower(filepath.Ext(foundIcon)) == ".png" {
		if err := copyFile(foundIcon, outputPath); err != nil {
			result.Error = fmt.Errorf("failed to copy icon: %w", err)
			return result
		}
	} else {
		if err := convertImageToPng(foundIcon, outputPath); err != nil {
			result.Error = fmt.Errorf("failed to convert icon to PNG: %w", err)
			return result
		}
	}

	result.Success = true
	result.IconPath = outputPath
	return result
}

// Helper functions

func iconToImage(hIcon uintptr) (image.Image, error) {
	var iconInfo ICONINFO
	ret, _, _ := procGetIconInfo.Call(hIcon, uintptr(unsafe.Pointer(&iconInfo)))
	if ret == 0 {
		return nil, fmt.Errorf("GetIconInfo failed")
	}
	defer procDeleteObject.Call(iconInfo.hbmColor)
	defer procDeleteObject.Call(iconInfo.hbmMask)

	// Get bitmap info
	var bitmapInfo BITMAPINFOHEADER
	bitmapInfo.biSize = uint32(unsafe.Sizeof(bitmapInfo))
	
	dc, _, _ := procCreateCompatibleDC.Call(0)
	defer procDeleteDC.Call(dc)

	// Get bitmap dimensions
	procGetDIBits.Call(
		dc,
		iconInfo.hbmColor,
		0,
		0,
		0,
		uintptr(unsafe.Pointer(&bitmapInfo)),
		0,
	)

	// For simplicity, use a PowerShell approach to convert
	// This is more reliable for complex icons
	return nil, fmt.Errorf("direct API conversion not fully implemented, use fallback")
}

func resizeIconTo512(img image.Image) image.Image {
	bounds := img.Bounds()
	if bounds.Dx() == 512 && bounds.Dy() == 512 {
		return img
	}

	dst := image.NewRGBA(image.Rect(0, 0, 512, 512))
	draw.BiLinear.Scale(dst, dst.Bounds(), img, bounds, draw.Src, nil)
	return dst
}

func savePNG(img image.Image, path string) error {
	f, err := os.Create(path)
	if err != nil {
		return err
	}
	defer f.Close()

	return png.Encode(f, img)
}

func convertIcoToPng(icoPath, pngPath string) error {
	// Use PowerShell to convert .ico to .png
	psScript := fmt.Sprintf(`
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::new('%s')
$bitmap = $icon.ToBitmap()
# Get the largest size
$size = 512
if ($bitmap.Width -gt $size -or $bitmap.Height -gt $size) {
    $bitmap = New-Object System.Drawing.Bitmap($bitmap, $size, $size)
}
$bitmap.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
$icon.Dispose()
`, icoPath, pngPath)

	out, err := exec.Command("powershell", "-NoProfile", "-Command", psScript).CombinedOutput()
	if err != nil {
		return fmt.Errorf("PowerShell conversion failed: %w, output: %s", err, string(out))
	}

	return nil
}

func convertImageToPng(imagePath, pngPath string) error {
	// Generic image conversion using PowerShell
	psScript := fmt.Sprintf(`
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile('%s')
$size = 512
if ($img.Width -gt $size -or $img.Height -gt $size) {
    $bitmap = New-Object System.Drawing.Bitmap($img, $size, $size)
    $bitmap.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
} else {
    $img.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png)
}
$img.Dispose()
`, imagePath, pngPath, pngPath)

	out, err := exec.Command("powershell", "-NoProfile", "-Command", psScript).CombinedOutput()
	if err != nil {
		return fmt.Errorf("image conversion failed: %w, output: %s", err, string(out))
	}

	return nil
}

func parseAppxManifestForLogo(manifestPath string) (string, error) {
	// Simple XML parsing for logo path
	data, err := os.ReadFile(manifestPath)
	if err != nil {
		return "", err
	}

	content := string(data)
	
	// Look for Logo or Square150x150Logo tags
	logoTags := []string{
		`<Logo>`,
		`<Square150x150Logo>`,
		`<Square310x310Logo>`,
		`<Square71x71Logo>`,
	}

	for _, tag := range logoTags {
		start := strings.Index(content, tag)
		if start != -1 {
			start += len(tag)
			end := strings.Index(content[start:], "<")
			if end != -1 {
				return strings.TrimSpace(content[start : start+end]), nil
			}
		}
	}

	return "", fmt.Errorf("no logo found in manifest")
}

func findBestLogoVariant(dir, baseName string) string {
	// Look for scale variants (scale-200, scale-400, etc.)
	scales := []string{"scale-400", "scale-200", "scale-150", "scale-125", "scale-100"}
	exts := []string{".png", ".jpg", ".jpeg"}

	for _, scale := range scales {
		for _, ext := range exts {
			path := filepath.Join(dir, baseName+"."+scale+ext)
			if fileExists(path) {
				return path
			}
		}
	}

	// Try without scale
	for _, ext := range exts {
		path := filepath.Join(dir, baseName+ext)
		if fileExists(path) {
			return path
		}
	}

	return ""
}

func parseNuspecForIcon(nuspecPath string) string {
	data, err := os.ReadFile(nuspecPath)
	if err != nil {
		return ""
	}

	content := string(data)
	start := strings.Index(content, "<icon>")
	if start == -1 {
		start = strings.Index(content, "<iconUrl>")
		if start == -1 {
			return ""
		}
		start += len("<iconUrl>")
		end := strings.Index(content[start:], "<")
		if end != -1 {
			iconURL := strings.TrimSpace(content[start : start+end])
			// If it's a file path, not a URL
			if !strings.HasPrefix(iconURL, "http") {
				return iconURL
			}
		}
		return ""
	}

	start += len("<icon>")
	end := strings.Index(content[start:], "<")
	if end != -1 {
		return strings.TrimSpace(content[start : start+end])
	}

	return ""
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func copyFile(src, dst string) error {
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	return os.WriteFile(dst, data, 0644)
}

// GenerateDefaultIcon creates a simple default icon for a given name
func GenerateDefaultIcon(name, outputPath string) error {
	// Create a simple 512x512 colored square with the first letter
	img := image.NewRGBA(image.Rect(0, 0, 512, 512))
	
	// Fill with a color based on the first letter
	var r, g, b uint8 = 100, 100, 200
	if len(name) > 0 {
		char := name[0]
		r = uint8((int(char) * 37) % 256)
		g = uint8((int(char) * 73) % 256)
		b = uint8((int(char) * 139) % 256)
	}

	// Create the color
	clr := color.RGBA{R: r, G: g, B: b, A: 255}

	for y := 0; y < 512; y++ {
		for x := 0; x < 512; x++ {
			img.Set(x, y, clr)
		}
	}

	return savePNG(img, outputPath)
}
