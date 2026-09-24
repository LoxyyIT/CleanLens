<p align="center">
  <img src="docs/assets/images/cleanlens-mark.svg" alt="CleanLens" width="88">
</p>

<h1 align="center">CleanLens</h1>

<p align="center">
  <strong>Remove the app. See what it leaves behind.</strong><br>
  A local-first Windows uninstaller that puts the cleanup decision in your hands.
</p>

<p align="center">
  <a href="https://loxyyit.github.io/CleanLens/">Project website</a> ·
  <a href="https://github.com/LoxyyIT/CleanLens">Source repository</a> ·
  <a href="CONTRIBUTING.md">Contributing</a> ·
  <a href="SECURITY.md">Security</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows-0b6cff" alt="Windows">
  <img src="https://img.shields.io/badge/UI-WPF-1677ff" alt="WPF">
  <img src="https://img.shields.io/badge/runtime-.NET%2010-512bd4" alt=".NET 10">
  <img src="https://img.shields.io/badge/license-MIT-18a06f" alt="MIT">
  <img src="https://img.shields.io/badge/status-early%20development-c78b2f" alt="Early development">
</p>

## Why CleanLens

An application's official uninstaller can leave settings, caches and other application data behind. CleanLens aims to make that cleanup understandable: see the path, see why it matched, and choose whether to move it.

The project is in early development. The current source is a deliberately narrow Windows prototype; it is not yet the complete uninstaller described in the long-term roadmap.

## What works in this source tree

- Reads registered uninstall entries from HKLM and HKCU in both 32-bit and 64-bit Registry views.
- Searches the inventory by name, publisher, version, executable metadata and install path.
- Displays registered version, install location, size estimate and uninstall command.
- Starts a registered uninstaller only after showing the command for confirmation.
- Enables candidate review only after CleanLens launched that uninstaller and a fresh registry scan no longer lists the application.
- Scans exact product-named folders and exact publisher/product directory pairs under AppData and ProgramData only after a registry-confirmed uninstall.
- Shows why a folder matched and leaves every candidate unselected.
- Moves reviewed folders into a local quarantine and can restore them when the original path is free.
- Stores operation history and quarantine metadata in a local SQLite database.
- Keeps the safety acknowledgement and application data on the device.

## Safety boundaries

CleanLens does not scan or offer Documents, Desktop, Pictures, Music, Videos or Saved Games. It does not delete Registry entries, services, scheduled tasks, startup entries, browser extensions, drivers or shared runtimes. The current data scan checks exact product-named folders directly under standard application-data roots and exact publisher/product directory pairs; every result is medium confidence, requires manual review and remains unselected. It is not a comprehensive leftover scanner.

Cleanup moves a selected folder to quarantine. It refuses paths outside configured application-data roots, protected Windows and Program Files paths, known personal libraries, and paths containing reparse points. This reduces risk but cannot eliminate races caused by other software changing filesystem state concurrently. A quarantine move is not a guarantee that an application can be fully restored.

An uninstall command comes from the Windows Registry and is untrusted input. CleanLens displays it before launching it. The command is executed by Windows as registered; inspect the executable and arguments and cancel if they are unexpected. The application's own uninstaller controls its removal behavior.

CleanLens starts only an existing, fully qualified executable path, apart from MSI commands which are resolved to Windows' system msiexec.exe. It refuses quiet-only uninstall entries. Authenticode signer verification of third-party uninstallers is not implemented.

## Interface

The WPF application currently has an English interface. The static project website supports English, Italian, Spanish and French. Localized desktop UI, appearance settings, advanced inventory filters and accessibility validation are still planned.

No verified desktop screenshots are included yet. The website's product illustration is labelled as a concept, not a real capture.

## Build

Requirements: Windows, .NET 10 SDK and a network connection for the first NuGet restore.

    dotnet restore CleanLens.sln
    dotnet build CleanLens.sln -c Release
    dotnet test CleanLens.sln -c Release

Create a self-contained Windows x64 publish directory:

    dotnet publish src/CleanLens.App/CleanLens.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/CleanLens-win-x64

The publish directory is portable at the .NET runtime level. A signed installer, packaged release, updater and published binaries do not exist yet.

## Architecture

| Project | Responsibility |
| --- | --- |
| CleanLens.App | WPF desktop interface and local dependency setup |
| CleanLens.Core | Application and candidate models, confidence and path policy |
| CleanLens.Windows | Registry inventory, registered uninstaller launch, limited folder scan and quarantine |
| CleanLens.Data | Local SQLite history and quarantine metadata |
| tests/CleanLens.Core.Tests | Safety, parser and sandboxed quarantine tests |
| docs/ | Static GitHub Pages website |

## Roadmap

See ROADMAP.md for planned work and current boundaries. MSIX/AppX inventory, services, scheduled tasks, startup components, file signatures, real measured disk usage, install monitoring, complete desktop localization, light/dark/system themes and broader validation are not implemented.

## Privacy

The desktop application has no telemetry, analytics, account or cloud component. Inventory scans and cleanup happen locally. History and quarantine records are stored below %LOCALAPPDATA%\CleanLens.

## Contributing

Read CONTRIBUTING.md and SECURITY.md before proposing changes to path handling or cleanup behavior. Destructive tests must use temporary fixtures only.

## License

CleanLens is licensed under the MIT License. See LICENSE.
