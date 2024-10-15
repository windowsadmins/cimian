# Define installation paths
$installPath = "C:\Program Files\Gorilla\bin"
$configPath = "C:\ProgramData\Gorilla"

# Ensure directories exist
New-Item -ItemType Directory -Path $installPath, $configPath -Force | Out-Null

# Copy all .exe files to the bin directory
$exeFiles = Get-ChildItem "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)\*.exe"
foreach ($exe in $exeFiles) {
    Write-Host "Copying $($exe.Name) to $installPath"
    Copy-Item $exe.FullName -Destination $installPath -Force
}

# Copy the configuration file to ProgramData
Write-Host "Copying config.yaml to $configPath"
Copy-Item "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)\config.yaml" -Destination $configPath -Force

# Add the bin directory to the system PATH
[System.Environment]::SetEnvironmentVariable(
    "PATH", 
    $installPath + ";" + [System.Environment]::GetEnvironmentVariable("PATH", [System.EnvironmentVariableTarget]::Machine), 
    [System.EnvironmentVariableTarget]::Machine
)
Write-Host "Added $installPath to system PATH"

# Confirm installation success
Write-Host "Gorilla installed successfully."
