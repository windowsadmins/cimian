![Cimian logo](cimian.png)
# Cimian

Munki-like Application Management for Windows

## About This Fork

This is a fork of the original Cimian project by Dustin Davis (https://github.com/1dustindavis/cimian). The goal of this fork is to extend Cimian's capabilities.

## Changes in this Fork so far:

- Implemented `cimianimport` and `makepkginfo` tools
- Added support for pkginfo files

## Original Description

Cimian is intended to provide application management on Windows using [Munki](https://github.com/munki/munki) as inspiration.
Cimian supports `.msi`, `.ps1`, `.exe`, or `.nupkg` [(via chocolatey)](https://github.com/chocolatey/choco).

## Getting Started
Information related to installing and configuring Cimian can be found on the [Wiki](https://github.com/windowsadmins/cimian/wiki).

## Building

If you just want the latest version, download it from the [releases page](https://github.com/windowsadmins/cimian/releases).

Building from source requires the [Go tools](https://golang.org/doc/install).

#### Windows
After cloning this repo, just run `go build -i ./cmd/cimian`. A new binary will be created in the current directory.

## Contributing
Pull Requests are always welcome. Before submitting, lint and test:
```
go fmt ./...
go test ./...
```

## License

This project is licensed under the Apache License, Version 2.0. See the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Dustin Davis and all contributors to the original Cimian project
- The Munki project, which served as inspiration for Cimian
