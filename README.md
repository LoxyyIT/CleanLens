<p align="center">
  <img src="logo.png" alt="CleanLens" width="120">
</p>

<h1 align="center">CleanLens</h1>

<p align="center">
  <strong>Remove the app. See what it leaves behind.</strong><br>
  A local-first Windows uninstaller that puts every cleanup decision in your hands.
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
- Shows registered Windows app icons in the inventory, with a monogram fallback when Windows has no icon.
- Offers a separate, opt-in manual delete review for exact folder-name matches in common personal folders. Each full path starts unselected and permanent deletion requires a second confirmation.
- Stores operation history and quarantine metadata in a local SQLite database.
- Persists the selected language and safety acknowledgement locally.

## Safety boundaries

The standard leftover scan checks exact product-named folders in AppData and ProgramData after an uninstall is confirmed. Manual delete lists Windows-registered and Windows Installer paths, Steam game files and related app-ID data from Steam manifests, exact app-data matches in AppData and ProgramData, and exact-name matches throughout accessible subfolders in common personal libraries. It never selects a result automatically, and a matching name is not proof that a folder belongs to the app. Review each path carefully; deletion is permanent and may remove program or personal files. It does not scan the whole disk or identify individual files by content.

Standard cleanup moves a selected application-data folder to quarantine. Manual deletion is a distinct, irreversible operation. Both actions revalidate selected paths and refuse paths outside supported roots and folders containing reparse points. This reduces risk but cannot eliminate races caused by other software changing filesystem state concurrently. A quarantine move is not a guarantee that an application can be fully restored.

**Warning:** inappropriate use or a wrong path can damage Windows, break applications or permanently remove personal files. Review the full path list and proceed only when you accept responsibility for the selected items.

An uninstall command comes from the Windows Registry and is untrusted input. CleanLens displays it before launching it. The command is executed by Windows as registered; inspect the executable and arguments and cancel if they are unexpected. The application's own uninstaller controls its removal behavior.

CleanLens starts only an existing, fully qualified executable path, apart from MSI commands which are resolved to Windows' system msiexec.exe. It refuses quiet-only uninstall entries. Authenticode signer verification of third-party uninstallers is not implemented.

## Interface

The WPF application and static website support English, Italian, Spanish and French. The desktop language choice is saved locally. Overview has been consolidated into the Applications page. Appearance themes and accessibility validation are still planned.

The project site includes an actual Windows capture of the CleanLens app. It shows a local inventory filtered to Microsoft Visual C++ entries; application data and inventory counts vary by PC.

## Download

The first self-contained Windows x64 build is available from [GitHub Releases](https://github.com/LoxyyIT/CleanLens/releases/latest). Download the portable ZIP, extract it and run `CleanLens.exe`. The build is unsigned and does not include an installer or updater; Windows SmartScreen may show a warning.

## Build

Requirements: Windows, .NET 10 SDK and a network connection for the first NuGet restore.

    dotnet restore CleanLens.sln
    dotnet build CleanLens.sln -c Release
    dotnet test CleanLens.sln -c Release

Create a self-contained Windows x64 publish directory:

    dotnet publish src/CleanLens.App/CleanLens.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/CleanLens-win-x64

The publish directory includes the .NET runtime and is suitable for packaging as a portable ZIP. The current public build is unsigned; a signed installer and updater are not provided.

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
