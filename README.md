# CleanLens

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
  <a href="https://github.com/LoxyyIT/CleanLens/releases/tag/v0.4.1"><img src="https://img.shields.io/badge/release-v0.4.1-396be8" alt="CleanLens 0.4.1 release"></a>
  <img src="https://img.shields.io/badge/license-MIT-18a06f" alt="MIT">
  <img src="https://img.shields.io/badge/status-early%20development-c78b2f" alt="Early development">
</p>

<p align="center"><img src="logo.png" alt="CleanLens" width="110"></p>

## Contents

- [Why CleanLens](#why-cleanlens)
- [Quick start](#quick-start)
- [Features](#features)
- [Using CleanLens](#using-cleanlens)
- [Safety boundaries](#safety-boundaries)
- [Interface](#interface)
- [Download](#download)
- [Build](#build)
- [Architecture](#architecture)
- [Roadmap](#roadmap)
- [Privacy](#privacy)
- [Contributing](#contributing)
- [License](#license)

## Why CleanLens

An application's official uninstaller can leave settings, caches and other application data behind. CleanLens aims to make that cleanup understandable: see the path, see why it matched, and choose whether to move it.

CleanLens combines an installed-app inventory, reviewed cleanup paths and an on-demand disk explorer. It is local-first: you inspect the paths and choose what happens to them.

## Quick start

1. Download the [latest Windows x64 portable ZIP](https://github.com/LoxyyIT/CleanLens/releases/latest).
2. Extract the ZIP to a folder and run `CleanLens.exe`.
3. Review the safety notice, then scan installed applications or open **Scansione rapida** to check common risk folders with Microsoft Defender. You can also use **Disco** to inspect a chosen drive or folder.

The ZIP includes the .NET runtime. CleanLens is unsigned and has no installer or updater; Windows SmartScreen may show a warning. It does not request administrator access at startup. The focused Defender scan asks for UAC approval only when you start it; a denied quarantine move still explains how to restart with administrator permissions and retry.

## Features

- **Installed app inventory:** reads uninstall entries from HKLM and HKCU in both 32-bit and 64-bit Registry views, and lists AppX/MSIX packages registered for the current Windows user. Windows-marked non-removable packages are blocked from removal. Windows app icons are used when available.
- **Search, filters and selection:** search all fields or narrow by app name, publisher, version or install path. Filter by uninstaller availability, publisher information and reported size. Checkbox-select one or multiple apps; multi-app cleanup is limited to Manual delete.
- **Reviewed uninstall:** inspect the registered command and Windows Authenticode trust result before launch. The signature belongs to the executable; for MSI entries CleanLens verifies Windows `msiexec.exe`, not the MSI package.
- **Leftover review:** after a fresh inventory confirms the app is no longer registered, scan exact product folders in AppData and ProgramData, plus matching Windows services, scheduled tasks and startup entries. System artifacts are read-only reports and cannot be quarantined or deleted from this page.
- **Manual delete scan:** separately review registered install and Windows Installer locations, Program Files and Program Files (x86), app-data folders in accessible Windows profiles, Steam app-ID paths and exact-name matches in supported personal libraries. Settings lists the default scan roots, lets you disable them individually, and lets you add or remove extra roots.
- **Measured disk usage:** on request, sum file lengths across matched install, application-data, cache and other candidate locations. This can include user data; unreadable paths are counted and marked as a partial measurement. The result is not allocated disk space.
- **Install Monitor:** start a local session before an installation, run the installer yourself, then stop the session to compare the registered-app inventory, selected service/startup Registry entries, and file system events under Program Files, the current profile's AppData, ProgramData, scheduled-task files and enabled custom roots. It does not read file contents. Reports are saved locally and flag watcher overflow or inaccessible paths.
- **Disk explorer:** explicitly scan a selected drive or folder, browse folders with Back and Parent navigation, filter by measured size, spot the largest results with proportional bars, inspect extension totals and explore a size-weighted treemap. ZIP files are measured as stored files and are never opened. A temporary SQLite index keeps scan results pageable without holding every path in RAM; it is removed when CleanLens closes.
- **Quick security scan:** run a Microsoft Defender custom scan against Downloads, Desktop, local temporary files and one optional folder. It uses Defender's installed engine and definitions, requires an administrator-approved UAC prompt, scans only these locations, and passes `-DisableRemediation` so detections are reported without automatic cleanup. CleanLens shows the Defender output and never deletes or quarantines a finding.
- **Duplicate review:** find same-size file candidates, then optionally verify contents with SHA-256. Only verified groups can be cleaned; review every path, keep at least one copy per group, and confirm permanent deletion after CleanLens checks the selected files again.
- **Saved disk scans:** keep local metadata snapshots and compare scans of the same drive or folder to find paths added, removed or changed. Snapshots contain scan metadata, not file contents; delete snapshots you no longer need to reclaim local storage.
- **Saved cleanup plans:** save selected disk paths with measured sizes and risk labels, then resume after a fresh scan of the same root. CleanLens checks exact paths against the new scan and asks for confirmation; plans never delete by themselves.
- **Local reports:** export the visible app inventory, leftover reasons, full indexed Disk scan, duplicate results, snapshot differences, quarantine or history as HTML or CSV. Reports are generated on the PC and are not uploaded.
- **Disk cleanup:** select exact file or folder paths and review them before permanent deletion. CleanLens lists the paths again in a separate confirmation. Disk deletion bypasses the Recycle Bin and cannot be undone.
- **Quarantine or permanent deletion:** from the manual path list, move selected files and folders into local quarantine for later restore, or permanently delete them after a separate confirmation. If Windows denies a move, CleanLens asks you to restart it as administrator; it does not elevate itself.
- **Local records and settings:** browse operation history and quarantined items, check whether each saved payload and original path is available, and restore one or several items when the original paths are free. Install Monitor sessions can be associated with the selected app and reviewed later. Settings and safety acknowledgements stay local.

## Using CleanLens

### Review installed apps

Scan the Windows inventory, then search or filter by name, publisher, version, install path, uninstaller availability or reported size. Select an app row to inspect its registered details. Checkboxes select one or several apps; multiple selection enables Manual delete only.

### Review leftovers and quarantine

After uninstalling an app and scanning the inventory again, open Leftover review to inspect exact application-data matches and read-only service, task and startup reports. Quarantine moves selected supported folders to a local restore location. Permanent deletion is a separate action with a separate confirmation.

### Explore a drive or folder

Open **Disco**, choose a drive or use **Sfoglia cartelle**, and start the scan. Double-click a folder or use **Apri cartella** to inspect its contents; **Indietro** returns to the previous folder and **Su** opens its parent. Sort the list by size, use the size filter to narrow results, switch to largest-item views, or open the treemap. In the size column, short bars compare each result with the total measured size of the scan. Scanning reads metadata, not file contents, and measures logical file lengths rather than allocated disk space.

### Run a quick security scan

Open **Scansione rapida** and start the scan. CleanLens asks Windows for administrator approval, then asks Defender to check Downloads, Desktop, local temporary files and any folder you add. The scan is limited to those locations and is not a full-device antivirus scan. Review Defender's output in CleanLens; the app does not take action on detected files. Defender's own cloud-protection and sample-submission settings still apply, so Windows may send samples to Microsoft according to those settings.

### Compare scans and prepare cleanup

After a Disk scan completes, save a snapshot before scanning the same root again. In **Analisi → Cronologia scansioni**, compare the snapshots. In **Analisi → Piani di pulizia**, save exact selected paths for later review. In **Analisi → File duplicati**, run SHA-256 verification before choosing copies to remove. Each destructive action shows the paths and asks for confirmation.

Use **Esporta report** to save the current inventory or report as HTML or CSV. Exports stay local. CSV values that could be evaluated as formulas are prefixed before writing.

## Safety boundaries

The standard leftover scan checks exact product-named folders in the current user's AppData and shared ProgramData only after an uninstall is confirmed. It also reports service, scheduled-task and startup matches as read-only entries. Manual delete checks the additional locations listed above, including user-configured search roots. It uses exact folder-name or publisher/product matches; a matching name is not proof that a path belongs to the app. Every result starts unchecked. Moving to quarantine is reversible when the original path is free; permanent deletion has a separate confirmation and may remove program or personal files.

The Disk page scans drives or folders only after you explicitly start a scan and reads file metadata, not file contents. The separate Quick security scan invokes Microsoft Defender on Downloads, Desktop, local temporary files and an optional folder. It is not a full-device antivirus scan and does not cover every Windows app type or every leftover location. The Defender custom scan ignores Defender file exclusions and checks archive contents. CleanLens displays the command output without deleting, quarantining or changing findings. Defender's cloud-protection and sample-submission behavior follows the settings already configured in Windows, which may send file samples to Microsoft. AppX inventory and removal apply to the current user; they do not provision or remove packages for other user profiles. Install Monitor observes only selected Registry areas and file system events under roots it can watch; it can lose events when Windows buffers overflow. Junctions and symbolic links are not followed by disk scan, and path checks cannot eliminate every race with other software.

The Disk page scans only after you start it. It skips junctions and symbolic-link targets, reports inaccessible entries, and measures logical file lengths rather than allocated disk space. Disk deletion bypasses the Recycle Bin and cannot be undone; it requires selecting and confirming each displayed path. A selected scan root cannot itself be deleted.

Standard cleanup moves a selected application-data folder to quarantine. Manual deletion is a distinct, irreversible operation. Both actions revalidate selected paths and refuse paths outside supported roots and folders containing reparse points. This reduces risk but cannot eliminate races caused by other software changing filesystem state concurrently. A quarantine move is not a guarantee that an application can be fully restored.

**Warning:** inappropriate use or a wrong path can damage Windows, break applications or permanently remove personal files. Review the full path list and proceed only when you accept responsibility for the selected items.

An uninstall command comes from the Windows Registry and is untrusted input. CleanLens displays it and verifies the executable with Windows Authenticode before launch. An untrusted or unsigned result is shown for review; the user still decides whether to continue. For MSI commands, only the system `msiexec.exe` signature is checked, not the installer package. The command is executed by Windows as registered; inspect the executable and arguments and cancel if they are unexpected. The application's own uninstaller controls its removal behavior.

CleanLens starts only an existing, fully qualified executable path, apart from MSI commands which are resolved to Windows' system msiexec.exe. It refuses quiet-only uninstall entries.

## Interface

The WPF application and static website support English, Italian, Spanish and French. The desktop language choice is saved locally. The app uses custom window controls and a shared visual system, with native Windows file icons and multi-selection. Appearance themes and a full accessibility validation are still planned.

## Download

The current self-contained Windows x64 build is available from [GitHub Releases](https://github.com/LoxyyIT/CleanLens/releases/latest). Download the portable ZIP, extract it and run `CleanLens.exe`. The build is unsigned and does not include an installer or updater; Windows SmartScreen may show a warning.

The latest published portable ZIP is v0.4.1. The release page also includes a SHA-256 checksum for the download.

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
| CleanLens.Windows | Windows inventory, uninstaller review, disk scanning/indexing, cleanup scans and quarantine |
| CleanLens.Data | Local SQLite history and quarantine metadata |
| tests/CleanLens.Core.Tests | Safety, parser and sandboxed quarantine tests |
| docs/ | Static GitHub Pages website |

## Roadmap

See ROADMAP.md for planned work and current boundaries. Current source includes per-user AppX removal, measured app usage, Install Monitor, configurable manual search roots, read-only service/task/startup detection, duplicate review, saved disk snapshots, cleanup plans and local reports. Cleanup support for system artifacts, all-user package management, allocated-space measurement, themes and broader compatibility validation remain future work.

## Privacy

CleanLens itself has no telemetry, analytics, account or cloud service, and does not upload scan reports. Inventory scans and cleanup happen locally. The optional Microsoft Defender scan follows Defender's existing cloud-protection and sample-submission settings; depending on those settings, Windows may send file samples to Microsoft. History and quarantine records are stored below %LOCALAPPDATA%\CleanLens. The Disk page's temporary metadata index is also stored there and removed when CleanLens closes.

## Contributing

Read CONTRIBUTING.md and SECURITY.md before proposing changes to path handling or cleanup behavior. Destructive tests must use temporary fixtures only.

## License

CleanLens is licensed under the MIT License. See LICENSE.
