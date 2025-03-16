# Cimian

<img src="cimian.png" alt="Cimian" width="300">

Cimian is an open-source software deployment solution designed specifically for managing and automating software installations on Windows systems. Heavily inspired by Munki, Cimian allows Windows administrators to efficiently manage software packages through a webserver-based repository of packages and metadata, enabling automated deployments, updates, and removals at scale.

Cimian simplifies the software lifecycle management process, from creating packages to deploying them securely via Microsoft Intune or other cloud providers, ensuring consistency and reliability across large-scale Windows deployments.

## Key Features

- **Automated Package Management**: Streamline software packaging, metadata management, and distribution.
- **Flexible YAML Configuration**: Easily configure and manage settings through clear, YAML-based config files.
- **Multi-format Installer Support**: Supports MSI, MSIX, EXE, and NuGet package formats.

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
