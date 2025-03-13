// pkg/extract/msi.go - functions for extracting metadata from MSI files.

package extract

import (
	"encoding/json"
	"fmt"
	"os/exec"
	"runtime"
	"strings"
)

// MsiMetadata now returns 6 values: name, version, developer, description, productCode, upgradeCode.
func MsiMetadata(msiPath string) (productName, productVersion, developer, description, productCode, upgradeCode string) {
	if runtime.GOOS != "windows" {
		return "UnknownMSI", "", "", "", "", ""
	}

	// PowerShell script now also retrieves ProductCode & UpgradeCode
	psCommand := fmt.Sprintf(`
$msi = "%s"
$WindowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$db = $WindowsInstaller.OpenDatabase($msi,0)
$view = $db.OpenView('SELECT * FROM Property')
$view.Execute()

$pairs = @{}
while($rec = $view.Fetch()) {
    $prop = $rec.StringData(1)
    $val = $rec.StringData(2)
    $pairs[$prop] = $val
}
$props = [PSCustomObject]@{
  ProductName   = $pairs["ProductName"]
  ProductVersion= $pairs["ProductVersion"]
  Manufacturer  = $pairs["Manufacturer"]
  Comments      = $pairs["Comments"]
  ProductCode   = $pairs["ProductCode"]
  UpgradeCode   = $pairs["UpgradeCode"]
}
$props | ConvertTo-Json -Compress
`, msiPath)

	out, err := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", psCommand).Output()
	if err != nil {
		// If we fail to run PowerShell, set minimal defaults
		return "UnknownMSI", "", "", "", "", ""
	}

	var props map[string]string
	if e := json.Unmarshal(out, &props); e != nil {
		return "UnknownMSI", "", "", "", "", ""
	}

	// Extract or fallback
	productName = strings.TrimSpace(props["ProductName"])
	productVersion = strings.TrimSpace(props["ProductVersion"])
	developer = strings.TrimSpace(props["Manufacturer"])
	description = strings.TrimSpace(props["Comments"])
	productCode = strings.TrimSpace(props["ProductCode"])
	upgradeCode = strings.TrimSpace(props["UpgradeCode"])

	// Set a default name if none
	if productName == "" {
		productName = "UnknownMSI"
	}
	return productName, productVersion, developer, description, productCode, upgradeCode
}
