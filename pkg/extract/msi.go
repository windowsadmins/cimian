// pkg/extract/msi.go

package extract

import (
	"encoding/json"
	"fmt"
	"os/exec"
	"runtime"
	"strings"
)

// parse .msi with PowerShell or advanced approach
func MsiMetadata(msiPath string) (productName, productVersion, developer, description string) {
	if runtime.GOOS != "windows" {
		return "UnknownMSI", "", "", ""
	}
	out, err := exec.Command("powershell", "-Command", fmt.Sprintf(`
$msi = "%s"
$WindowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$db = $WindowsInstaller.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$WindowsInstaller,@($msi,0))
$view = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@('SELECT * FROM Property'))
$view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null)
$pairs = @{}
while($rec = $view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)) {
  $prop = $rec.StringData(1)
  $val = $rec.StringData(2)
  $pairs[$prop] = $val
}
$pairs | ConvertTo-Json -Compress
`, msiPath)).Output()
	if err != nil {
		return "UnknownMSI", "", "", ""
	}
	var props map[string]string
	if e := json.Unmarshal(out, &props); e != nil {
		return "UnknownMSI", "", "", ""
	}
	productName = strings.TrimSpace(props["ProductName"])
	productVersion = strings.TrimSpace(props["ProductVersion"])
	developer = strings.TrimSpace(props["Manufacturer"])
	description = strings.TrimSpace(props["Comments"])
	if productName == "" {
		productName = "UnknownMSI"
	}
	return productName, productVersion, developer, description
}
