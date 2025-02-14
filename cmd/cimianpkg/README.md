## gorillapkg

`gorillapkg` is a tool for building `.nupkg` packages for deploying software on Windows in a consistent, repeatable manner. It leverages **NuGet** for package creation and **Chocolatey** (or **Gorilla**) for deployment, with support for **pre- and post-installation scripts** and **code signing**.

This tool simplifies the complexities of deployment by abstracting YAML-based configuration and script-based actions and offers **flexible certificate signing** using Windows `SignTool`.

### Features

- **Dynamic File Inclusion**: Automatically packages all files and folders in the `payload/` directory.
- **Script Support**: Supports **pre-install** and **post-install** scripts executed with **elevated privileges**.
- **Custom Installation Paths**: Configure where the payload files are installed via the YAML configuration.
- **Post-Install Actions**: Supports automatic **logout** or **restart** after package installation.
- **Package Signing with `SignTool`**: Allows seamless signing of `.nupkg` packages using a `.pfx` certificate or certificate store.
- **Smart Readme Inclusion**: Automatically creates and includes a `readme.md` if a description is provided in `build-info.yaml`.
- **Automated Packaging**: Uses the `nuget` CLI to build `.nupkg` packages for deployment with **Chocolatey** or **Gorilla**.
- **YAML-Driven Configuration**: All metadata and installation instructions come from the `build-info.yaml` file.

### Prerequisites

#### For Development:
- **Go** (to build the `gorillapkg` tool).
- **NuGet CLI** (for generating `.nupkg` packages).

#### For Deployment:
- **Chocolatey** or **Gorilla** (for installing `.nupkg` packages).
- **PowerShell** (to run pre- and post-installation scripts).
- **Windows SDK** (for the `SignTool` utility).

### Installation

Clone the repository:

```shell
git clone https://github.com/rodchristiansen/gorilla-pkg.git
cd gorilla-pkg
```

### Folder Structure for Packages

```
project/
├── payload/                   # Files/folders to be written to disk
│   └── example.txt
├── scripts/                   # Pre-/Post-install scripts
│   ├── preinstall.ps1         # Runs before files are installed
│   └── postinstall.ps1        # Runs after files are installed
└── build-info.yaml            # Metadata for package generation
```

### YAML Configuration: `build-info.yaml`

The `build-info.yaml` file contains configuration settings for the package:

```yaml
product:
  name: "Gorilla"
  version: "2024.10.11"
  identifier: "com.gorillacorp.gorilla"
  publisher: "Gorilla Corp"
  description: "This is the StartSet installer package."
install_location: "C:\Program Files\Gorilla"
postinstall_action: "none"
signing_certificate: "Gorilla Corp EV Certificate"
```

Here’s the **Field Descriptions** section updated with the `description` field information directly included.

#### Field Descriptions

- **`identifier`**:  
  A unique identifier in reverse-domain style (e.g., `com.gorillacorp.gorilla`). This ensures the package is correctly recognized by the system and prevents naming conflicts.

- **`version`**:  
  Supports **semantic versioning** (e.g., `1.0.0`) or **date-based versioning** (e.g., `2024.10.11`). Used to determine whether a new installation or update is required during deployments.

- **`name`**:  
  The display name of the product. This name will be visible during installation and in package managers like Chocolatey.

- **`publisher`**:  
  The organization or individual distributing the package. This helps users identify the source of the software and improves trust.

- **`description`**:  
  A brief description of the package's purpose or functionality. If provided, it will:
  - Be included in the `.nuspec` metadata.
  - Automatically generate a `readme.md` file to be packaged with the `.nupkg` for documentation purposes.
  - If the description is **absent**, no `readme.md` will be generated, and the process will proceed without errors or warnings.

- **`install_location`**:  
  The default directory where the software will be installed. This can be customized for each deployment.

- **`postinstall_action`**:  
  Specifies an optional action to take after installation:
  - `none`: No further action is taken after installation.
  - `logout`: Logs out the current user after installation completes.
  - `restart`: Restarts the system immediately after installation.

- **`signing_certificate`**:  
  Path to the `.pfx` certificate containing both the public and private keys, or the **name of a certificate** in the local certificate store. This certificate is used for **code signing** to ensure the package's authenticity and integrity.

### Usage

To create a new package:

```shell
gorillapkg <project_dir>
```

This command will:
1. Validate the project structure.
2. Convert `build-info.yaml` into a `.nuspec` manifest.
3. Run `nuget pack` to generate the `.nupkg`.
4. Optionally sign the package using the specified certificate.
5. Execute the specified post-install action (logout or restart).

### Script Execution

- **Pre-Install**:  
  `scripts/preinstall.ps1` runs **before** copying files.

- **Post-Install**:  
  `scripts/postinstall.ps1` runs **after** installation (acts like Chocolatey’s `chocolateyInstall.ps1`).

### Package Signing with `SignTool`

If a signing certificate is provided, `gorillapkg` will sign the package using Windows `SignTool`.

#### Using a .pfx Certificate

```shell
signtool sign /f "path\to\certificate.pfx" /p <password> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "path\to\package.nupkg"
```

#### Using the Certificate Store

```shell
signtool sign /n "Gorilla Corp EV Certificate" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "path\to\package.nupkg"
```

### Smart Readme Inclusion

- If the **`description`** field is provided in `build-info.yaml`, a `readme.md` will be generated and included in the package.
- If the description is **not** provided, the tool will skip the readme without errors.

### Example Commands

#### Building the `.nupkg`

```shell
.\gorillapkg.exe C:\Users\rchristiansen\DevOps\Gorilla\packages\StartSet
```

#### Installing with Chocolatey

```shell
choco install StartSet --source="C:\Users\rchristiansen\DevOps\Gorilla\packages\StartSet\build"
```

### Handling Post-Install Actions

The `postinstall_action` key in `build-info.yaml` triggers system actions:
- **`none`**: No action.
- **`logout`**: Logs out the user.
- **`restart`**: Restarts the system immediately.

### Example Output

```
2024/10/14 13:00:00 main.go:350: Using project directory: C:\Users\rchristiansen\DevOps\Gorilla\packages\StartSet
2024/10/14 13:00:00 main.go:356: Project structure verified. Proceeding with package creation...
2024/10/14 13:00:00 main.go:394: Package successfully created: StartSet.nupkg
2024/10/14 13:00:02 main.go:446: Package signed successfully: StartSet.nupkg
Executing post-install action: restart
Restarting system...
```

### Summary

`gorillapkg` streamlines the creation and deployment of `.nupkg` packages by:
- Automating **version control** and **metadata management** through YAML.
- Supporting **pre-install and post-install scripts**.
- Providing seamless **package signing** with `SignTool`.
- Offering **smart readme inclusion** based on the presence of a description.

This tool replaces complex MSI or WiX packaging with a simple, effective solution for Windows software deployment.
