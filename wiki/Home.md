# Cimian

Cimian is an open-source software deployment system for Windows, heavily inspired by
[Munki](https://github.com/munki/munki). You describe the software your machines should
have in plain YAML, serve that description and the installers from any static web
server, and a client agent on each machine makes the machine match. There is no push
infrastructure and no database — the repository is files, and the machines pull.

New here? Read [Overview](Overview) for the model, then [Getting Started](Getting-Started)
to put one package on one machine. If you already run Munki,
[Cimian for Munki Admins](Cimian-for-Munki-Admins) is the fastest route in.

## Introduction

| Page | |
|---|---|
| [Overview](Overview) | What Cimian is and how the pieces fit together |
| [Getting Started](Getting-Started) | From nothing to one machine installing one package |
| [Demonstration Setup](Demonstration-Setup) | Stand up a throwaway repo and client to try it |
| [Cimian for Munki Admins](Cimian-for-Munki-Admins) | Concept and tool mapping, and where the two differ |
| [Glossary](Glossary) | Every term the rest of the wiki uses |
| [Frequently Asked Questions](Frequently-Asked-Questions) | Short answers, honestly given |

## Installing and updating Cimian

| Page | |
|---|---|
| [Installing Cimian](Installing-Cimian) | Artifacts, what the MSI does, silent install, fleet deployment |
| [Removing Cimian](Removing-Cimian) | Uninstalling cleanly, and what is left behind |
| [Deploying Cimian With Intune](Deploying-Cimian-With-Intune) | Win32 app packaging and detection |
| [Bootstrapping With Cimian](Bootstrapping-With-Cimian) | Zero-touch first-boot provisioning |
| [Updating Cimian](Updating-Cimian) | How the client updates itself |

## Command-line tools

[Command-Line Tools](Command-Line-Tools) is the index. One page per binary:

| Client | Repository |
|---|---|
| [managedsoftwareupdate](managedsoftwareupdate) | [cimiimport](cimiimport) |
| [cimitrigger](cimitrigger) | [cimipkg](cimipkg) |
| [cimiwatcher](cimiwatcher) | [makepkginfo](makepkginfo) |
| [cimistatus](cimistatus) | [makecatalogs](makecatalogs) |
| | [manifestutil](manifestutil) |
| | [repoclean](repoclean) |

## Managed Software Center

| Page | |
|---|---|
| [Managed Software Center](Managed-Software-Center) | The end-user self-service application |
| [Optional Installs And Self Service](Optional-Installs-And-Self-Service) | Letting users choose their own software |
| [Featured Items](Featured-Items) | Promoting items in the interface |
| [Product Icons And Screenshots](Product-Icons-And-Screenshots) | Making items look right |

## Client configuration

| Page | |
|---|---|
| [Client Configuration](Client-Configuration) | Every preference key, and the policy override |
| [How Cimian Runs](How-Cimian-Runs) | Scheduled tasks, the watcher, run modes, running now |
| [Client Identifier Resolution](Client-Identifier-Resolution) | How a client finds its own manifest |
| [Configuring Clients With Intune](Configuring-Clients-With-Intune) | What an MDM can and cannot deliver |

## The repository

| Page | |
|---|---|
| [The Cimian Repository](The-Cimian-Repository) | Layout, serving it, creating one |
| [Using Catalogs](Using-Catalogs) | What catalogs are and how clients search them |
| [Promoting Between Catalogs](Promoting-Between-Catalogs) | Testing to production, safely |
| [Securing The Repository](Securing-The-Repository) | Authentication, TLS and client certificates |
| [Cimian With Git](Cimian-With-Git) | Keeping the repository in version control |

## Manifests

| Page | |
|---|---|
| [Manifests](Manifests) | The key reference and a recommended layout |
| [Conditional Items](Conditional-Items) | Targeting a subset of machines |
| [Conditional Facts Reference](Conditional-Facts-Reference) | Every fact a condition can test |

## pkgsinfo

| Page | |
|---|---|
| [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files) | What a pkgsinfo is |
| [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys) | The complete key reference |
| [Installer Types](Installer-Types) | MSI, MSIX, EXE, PowerShell, nupkg, copy, nopkg |
| [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed) | The detection cascade |
| [Installs Arrays](Installs-Arrays) | Declaring what "installed" means |
| [Version Comparisons](Version-Comparisons) | How version strings are ordered |
| [Scripts In pkgsinfo](Scripts-In-pkgsinfo) | Every script hook and its exit-code contract |
| [Uninstalling Software](Uninstalling-Software) | Making an item removable, and removing it |
| [Blocking Applications](Blocking-Applications) | Deferring an install while an application is open |
| [Dependencies And Update Chains](Dependencies-And-Update-Chains) | `requires` and `update_for` |
| [Importing EXE Bundle Installers](Importing-EXE-Bundle-Installers) | Awkward bundles that register oddly |

## Common operations

| Page | |
|---|---|
| [Installing Software](Installing-Software) | The end-to-end walkthrough |
| [Promoting Between Catalogs](Promoting-Between-Catalogs) | Moving an item through your rings |
| [On Demand Items](On-Demand-Items) | Run-now items that never stay installed |

## Advanced

| Page | |
|---|---|
| [Preflight And Postflight Scripts](Preflight-And-Postflight-Scripts) | Client-side hooks around a run |
| [Install Loop Prevention](Install-Loop-Prevention) | LoopGuard, and diagnosing a looping package |
| [Force Installs And Deadlines](Force-Installs-And-Deadlines) | `force_install_after_date` |
| [The Download Cache](The-Download-Cache) | Reuse, retention, and keeping it from filling the disk |
| [Managed Profiles And Managed Apps](Managed-Profiles-And-Managed-Apps) | What these keys do and do not do |

## Operations and troubleshooting

| Page | |
|---|---|
| [Troubleshooting](Troubleshooting) | Symptom-first diagnosis |
| [Logging](Logging) | What the client writes, and where |
| [Item Status Reference](Item-Status-Reference) | Every status an item can carry |
| [Reporting Data Contract](Reporting-Data-Contract) | The machine-readable output |

## Developing Cimian

| Page | |
|---|---|
| [Architecture](Architecture) | How the source is organised |
| [Building Cimian](Building-Cimian) | Building and testing from source |
| [Release Process](Release-Process) | Tagging, CI, and what CI does not do |
| [Contributing](Contributing) | Issues, branches, pull requests |
