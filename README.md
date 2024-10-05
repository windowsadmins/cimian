![Gorilla logo](gorilla.png)
# Gorilla

Munki-like Application Management for Windows

## About This Fork

This is a fork of the original Gorilla project by Dustin Davis (https://github.com/1dustindavis/gorilla). The goal of this fork is to extend Gorilla's capabilities.

## Changes in this Fork so far:

- Implemented `gorillaimport` and `makepkginfo` tools
- Added support for pkginfo files

## Original Description

Gorilla is intended to provide application management on Windows using [Munki](https://github.com/munki/munki) as inspiration.
Gorilla supports `.msi`, `.ps1`, `.exe`, or `.nupkg` [(via chocolatey)](https://github.com/chocolatey/choco).

## Getting Started
Information related to installing and configuring Gorilla can be found on the [Wiki](https://github.com/rodchristiansen/gorilla/wiki).

## Building

If you just want the latest version, download it from the [releases page](https://github.com/rodchristiansen/gorilla/releases).

Building from source requires the [Go tools](https://golang.org/doc/install).

#### Windows
After cloning this repo, just run `go build -i ./cmd/gorilla`. A new binary will be created in the current directory.

## Contributing
Pull Requests are always welcome. Before submitting, lint and test:
```
go fmt ./...
go test ./...
```

## License

This project is licensed under the Apache License, Version 2.0. See the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Dustin Davis and all contributors to the original Gorilla project
- The Munki project, which served as inspiration for Gorilla
