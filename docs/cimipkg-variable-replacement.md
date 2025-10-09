# cimipkg Variable Replacement Feature

## Overview

As of build 2025.10.08.1647, `cimipkg` now supports **environment variable placeholder replacement** in both `/scripts` files **and** `build-info.yaml`. This is a crucial feature for dynamic configuration values like API keys, credentials, installation paths, and other environment-specific settings.

## Supported Placeholder Patterns

`cimipkg` recognizes three different placeholder patterns:

### 1. PowerShell Style: `$env:VARIABLE_NAME`
```yaml
developer: $env:DEVELOPER_NAME
install_location: $env:INSTALL_PATH
```

### 2. Batch/CMD Style: `%VARIABLE_NAME%`
```yaml
api_endpoint: %API_ENDPOINT%
custom_field: %CUSTOM_VALUE%
```

### 3. Legacy Style: `VARIABLE_NAME_PLACEHOLDER`
```yaml
secret_token: SECRET_TOKEN_PLACEHOLDER
license_key: LICENSE_KEY_PLACEHOLDER
```

## Environment Variable Sources

Variables are loaded from multiple sources with the following priority:

1. **`.env` file** (highest priority) - Auto-detected in project directory or specified with `-env` flag
2. **System environment variables** (lowest priority) - Only Cimian-prefixed variables (`CIMIAN_*` or `Cimian*`)

## How It Works

### In `/scripts` Directory

Script files (`.ps1`, `.sh`, `.cmd`, `.bat`) get variable replacement with PowerShell-style quoting:

**Original script:**
```powershell
Write-Host "API Key: $env:API_KEY"
Write-Host "Install Path: $env:INSTALL_PATH"
```

**After replacement (PowerShell gets quotes):**
```powershell
Write-Host "API Key: "sk-test-12345abcdef""
Write-Host "Install Path: "C:\Program Files\MyApp""
```

### In `build-info.yaml`

YAML files get variable replacement **without adding quotes** to prevent YAML parsing errors:

**Original build-info.yaml:**
```yaml
product:
  name: MyApp
  version: 1.0.0
  developer: $env:DEVELOPER_NAME
  description: Test application with API Key $env:API_KEY
install_location: $env:INSTALL_PATH
postinstall_action: none
signing_certificate: EmilyCarrU Intune Windows Enterprise Certificate
```

**After replacement (no quotes added):**
```yaml
product:
  name: MyApp
  version: 1.0.0
  developer: ACME Corporation
  description: Test application with API Key sk-test-12345abcdef
install_location: C:\Program Files\MyApp
postinstall_action: none
signing_certificate: EmilyCarrU Intune Windows Enterprise Certificate
```

## Usage Examples

### Example 1: Basic Setup with .env File

**Project structure:**
```
MyProject/
├── build-info.yaml
├── .env
├── scripts/
│   └── postinstall.ps1
└── payload/
    └── app.exe
```

**`.env` file:**
```bash
# Environment variables for MyProject
DEVELOPER_NAME=ACME Corporation
API_KEY=sk-prod-xyz789
INSTALL_PATH=C:\Program Files\MyApp
SECRET_TOKEN=my-secret-value
```

**`build-info.yaml`:**
```yaml
product:
  name: MyApp
  version: 1.0.0
  developer: $env:DEVELOPER_NAME
  identifier: com.acme.myapp
  description: Application with embedded API key $env:API_KEY
install_location: $env:INSTALL_PATH
postinstall_action: none
signing_certificate: EmilyCarrU Intune Windows Enterprise Certificate
```

**`scripts/postinstall.ps1`:**
```powershell
Write-Host "Installing MyApp"
Write-Host "Developer: $env:DEVELOPER_NAME"
Write-Host "Configuring API Key: $env:API_KEY"
Write-Host "Install location: $env:INSTALL_PATH"

# Configure application with secret
Set-Content -Path "$env:ProgramData\MyApp\config.txt" -Value "TOKEN=$env:SECRET_TOKEN"
```

**Build command:**
```powershell
.\cimipkg.exe MyProject
```

The `.env` file is auto-detected and applied to both `build-info.yaml` and all scripts.

### Example 2: Explicit .env File Path

```powershell
.\cimipkg.exe -env "C:\Configs\production.env" MyProject
```

### Example 3: Multiple Placeholder Styles

**`.env` file:**
```bash
DEVELOPER_NAME=ACME Corp
API_ENDPOINT=https://api.example.com
SECRET_TOKEN=abc123xyz
```

**`build-info.yaml` using all three styles:**
```yaml
product:
  name: MultiStyleApp
  version: 1.0.0
  developer: $env:DEVELOPER_NAME        # PowerShell style
  identifier: com.acme.multistyle
install_location: C:\Apps
api_endpoint: %API_ENDPOINT%            # Batch style
secret_token: SECRET_TOKEN_PLACEHOLDER  # Legacy style
```

All three patterns will be replaced with their corresponding environment variable values.

## Package Types and Variable Replacement

### .pkg Packages (Default)

For `.pkg` packages (sbin-installer format):

1. Scripts in `/scripts` directory are processed with variable replacement
2. `build-info.yaml` is processed with variable replacement
3. Both are then included in the `.pkg` ZIP archive
4. PowerShell scripts are code-signed **after** variable replacement

**Build command:**
```powershell
.\cimipkg.exe MyProject
```

### .nupkg Packages (Legacy Chocolatey)

For `.nupkg` packages:

1. Pre-install scripts (`preinstall*.ps1`) are bundled into `chocolateyBeforeModify.ps1` with variable replacement
2. Post-install scripts (`postinstall*.ps1`) are appended to `chocolateyInstall.ps1` with variable replacement
3. Environment variables are injected at the top of generated scripts
4. Generated scripts are code-signed

**Build command:**
```powershell
.\cimipkg.exe -nupkg MyProject
```

## Security Considerations

### Sensitive Data in .env Files

**IMPORTANT:** Never commit `.env` files containing sensitive data to version control!

**Recommended `.gitignore` entry:**
```gitignore
# Environment files with secrets
.env
*.env
!.env.template
```

**Best practice:** Create a `.env.template` with placeholder values:

**`.env.template` (safe to commit):**
```bash
# Template for environment variables
# Copy this to .env and fill in actual values
DEVELOPER_NAME=Your Company Name
API_KEY=your-api-key-here
INSTALL_PATH=C:\Program Files\YourApp
SECRET_TOKEN=your-secret-token
```

### Embedded Secrets in Packages

Variables are **permanently embedded** into the package contents:
- In `build-info.yaml` for `.pkg` packages
- In generated PowerShell scripts for both package types

**Implications:**
- Anyone with access to the package can extract and read the values
- Use this for non-secret configuration (company names, URLs, paths)
- For highly sensitive secrets (passwords, API keys), consider:
  - Runtime retrieval from secure vault (Azure Key Vault, etc.)
  - Encrypted configuration files
  - Registry-based CSP configuration

## Debugging Variable Replacement

### Enable Verbose Logging

```powershell
.\cimipkg.exe -verbose MyProject
```

**Output shows:**
```
Loaded 5 environment variables for script injection
Performed 5 environment variable replacements in script
Applied placeholder replacement to script: postinstall.ps1
Performed 3 environment variable replacements in YAML
Applied placeholder replacement to build-info.yaml
```

### Verify Package Contents

**For .pkg packages:**
```powershell
# Extract and inspect
Expand-Archive -Path "MyProject\build\MyApp-1.0.0.pkg" -DestinationPath "extracted"
Get-Content "extracted\build-info.yaml"
Get-Content "extracted\scripts\postinstall.ps1"
```

**For .nupkg packages:**
```powershell
# Rename to .zip and extract
Copy-Item "MyProject\build\MyApp-1.0.0.nupkg" "MyApp-1.0.0.zip"
Expand-Archive -Path "MyApp-1.0.0.zip" -DestinationPath "extracted"
Get-Content "extracted\tools\chocolateyInstall.ps1"
```

## Common Use Cases

### 1. Dynamic Installation Paths
```yaml
install_location: $env:CUSTOM_INSTALL_PATH
```

### 2. API Keys and Endpoints
```yaml
product:
  description: Service connecting to %API_ENDPOINT%
api_key: $env:SERVICE_API_KEY
```

### 3. Company Branding
```yaml
product:
  developer: $env:COMPANY_NAME
  identifier: com.$env:COMPANY_DOMAIN.$env:APP_NAME
```

### 4. Environment-Specific Configuration
```yaml
deployment_environment: $env:DEPLOY_ENV  # dev, staging, prod
log_level: $env:LOG_LEVEL
```

### 5. Build Metadata
```yaml
build_number: $env:BUILD_NUMBER
git_commit: $env:GIT_COMMIT_SHA
```

## Limitations and Known Issues

1. **No Nested Variable Expansion:** Variables cannot reference other variables
   ```yaml
   # This does NOT work:
   base_path: C:\Apps
   install_location: $env:base_path\MyApp  # base_path is not expanded
   ```

2. **YAML String Escaping:** Complex values with special characters may need quoting
   ```yaml
   # If value contains colons, quote the whole line:
   description: "URL: $env:API_ENDPOINT"
   ```

3. **Windows Paths in YAML:** Backslashes in replaced paths work correctly
   ```yaml
   # This works fine after replacement:
   install_location: $env:INSTALL_PATH  # Becomes: C:\Program Files\MyApp
   ```

## Migration from Manual Variable Injection

If you previously used custom scripts or manual replacement:

**Before (manual approach):**
```powershell
# Old workflow
$content = Get-Content build-info.yaml -Raw
$content = $content -replace 'API_KEY_HERE', $env:API_KEY
Set-Content build-info.yaml -Value $content
.\cimipkg.exe MyProject
```

**After (built-in support):**
```powershell
# New workflow - just build!
.\cimipkg.exe MyProject
# .env file automatically detected and applied
```

## Technical Implementation Details

### Two Replacement Functions

- **`replacePlaceholders()`**: For scripts - adds PowerShell quotes around values
- **`replacePlaceholdersYAML()`**: For YAML - no quotes (prevents parsing errors)

### Processing Order

1. Load environment variables from `.env` file (if present)
2. Merge with system Cimian-prefixed variables
3. Process `/scripts` directory files (if `.pkg` format)
4. Process `build-info.yaml` with YAML-safe replacement
5. Sign PowerShell scripts (after replacement)
6. Create package archive

### Code Signing Timing

**CRITICAL:** Variable replacement happens **BEFORE** code signing:

1. Scripts are processed with variable values
2. **Then** scripts are signed with Authenticode
3. Signature covers the final content with embedded values

This ensures signature integrity for the actual deployed content.

## Troubleshooting

### Issue: "error parsing YAML: found unknown escape character"

**Cause:** Windows paths with backslashes in YAML after replacement

**Solution:** This should not occur with the YAML-safe replacement. If it does, ensure you're using the latest version (2025.10.08.1647+)

### Issue: Variables not being replaced

**Checklist:**
1. ✅ Is the `.env` file in the project directory?
2. ✅ Are variable names spelled correctly in both files?
3. ✅ Run with `-verbose` to see replacement count
4. ✅ Check that `.env` file has proper KEY=value format

### Issue: Empty values in package

**Cause:** Variable not defined in `.env` or environment

**Solution:** 
```bash
# Check your .env file
REQUIRED_VAR=some_value  # Must have a value
```

Empty values are skipped during replacement.

## Future Enhancements

Potential future additions:
- [ ] Variable validation (fail build if required var is missing)
- [ ] Default values: `${VAR_NAME:-default_value}`
- [ ] Nested variable expansion
- [ ] Encrypted variable storage in `.env.encrypted`

## See Also

- [pkg Format Specification](pkg-format-specification.md)
- [Environment Variables Guide](csp-oma-uri-configuration.md)
- [Build System Documentation](../README.md)
