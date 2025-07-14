# Cimian

<img src="cimian.png" alt="Cimian" width="300">

Cimian is an open-source software deployment solution designed specifically for managing and automating software installations on Windows systems. **Heavily** inspired by the wonderful and dearly loved [Munki](https://github.com/munki/munki) project, Cimian allows Windows administrators to efficiently manage software packages through a webserver-based repository of packages and metadata, enabling automated deployments, updates, and removals at scale.

Cimian simplifies the software lifecycle management process, from creating packages to deploying them securely via Microsoft Intune or other cloud providers, ensuring consistency and reliability across large-scale Windows deployments.

## Key Features

- **Automated Package Management**: Streamline software packaging, metadata management, and distribution.
- **Flexible YAML Configuration**: Easily configure and manage settings through clear, YAML-based config files.
- **Multi-format Installer Support**: Supports MSI, MSIX, EXE, and NuGet package formats.
- **Bootstrap Mode**: Windows equivalent of Munki's bootstrap system for zero-touch deployment and system provisioning.

## Bootstrap System

Cimian includes a bootstrap system similar to Munki's, designed for zero-touch deployment scenarios where machines must complete all required software installations before users can log in.

### How Bootstrap Works

1. **Flag File**: `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` - When this file exists, Cimian enters bootstrap mode
2. **CimianWatcher Service**: A Windows service monitors the bootstrap flag file continuously and triggers installation automatically
3. **Non-Interactive Mode**: When in bootstrap mode, Cimian runs with a progress window and installs all required software without user interaction
4. **Automatic Cleanup**: The bootstrap flag file is automatically removed upon successful completion

### Bootstrap Commands

| Action          | Command                                                |
| --------------- | ------------------------------------------------------ |
| Enter bootstrap | `managedsoftwareupdate.exe --set-bootstrap-mode`       |
| Leave bootstrap | `managedsoftwareupdate.exe --clear-bootstrap-mode`     |

### Use Cases

- **Zero-touch deployment**: Ship Windows machines with only Cimian installed; bootstrap completes the configuration
- **System rebuilds**: Ensure all required software is installed before first user login
- **Provisioning automation**: Integrate with deployment tools for fully automated system setup

## Components

Cimian consists of the following core tools:

* `cimipkg`: Facilitates the creation of deployable NuGet packages.
* `cimiimport`: Automates importing software packages and generating metadata.
* `makecatalogs`: Generates software catalogs used to organize and manage software packages.
* `manifestutil`: Utility for managing deployment manifests.
* `managedsoftwareupdate`: Client-side component for handling software updates and maintenance tasks.


### Configuration

Configure your Cimian setup by editing the `config.yaml` file:

```yaml
software_repo_url: https://cimian.domain.com/
client_identifier: Bootstrap
force_basic_auth: false
default_arch: x64
default_catalog: Testing
```

## License

Cimian is distributed under the MIT License. See [LICENSE](LICENSE) for details.

## Contributing

We welcome contributions! Feel free to submit pull requests, report issues, or suggest improvements via our [GitHub repository](https://github.com/windowsadmins/cimian).
