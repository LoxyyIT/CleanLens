# Changelog

## 0.3.0 — Disk analysis and app-wide interface refresh

- Added same-size duplicate discovery with optional SHA-256 verification and guarded selection.
- Added saved Disk snapshots with same-root change comparison.
- Added locally saved cleanup plans that require a fresh scan and explicit confirmation before deletion.
- Added HTML and CSV report exports, with spreadsheet formula prefixes escaped in CSV values.
- Added multi-item quarantine restore and clear status for missing payloads or occupied original paths.
- Associated Install Monitor sessions with the selected app and made saved sessions reviewable in Settings.
- Added candidate match explanations and counts for read-only system leftovers.
- Refreshed shared desktop styling, app inventory rows, Disk empty state, analysis controls, Settings sections and dialog surfaces.
- Updated the README and four-language project website to document the new workflows.
- Distributed as an unsigned self-contained Windows x64 portable ZIP. No installer or updater is included.

## 0.2.1 — Feature guide refresh

- Expanded the README quick start, app selection, cleanup safety and Disk usage guidance.
- Updated the four-language website to describe the current app inventory, Monitor, quarantine and Disk explorer workflows.
- Refreshed the portable build metadata and links for v0.2.1.

## 0.2.0 — Disk explorer and desktop polish

- Added on-demand drive and folder scans with paged browsing, largest-item views, file-type totals and a size-weighted treemap.
- Added Windows file and folder icons, navigation history, folder breadcrumbs, and clearer scan progress.
- Added permanent disk-item deletion with exact-path review and confirmation.
- Added checkbox-based single and multiple app selection; multiple apps use Manual delete only.
- Reworked application and disk list rows, selection states, and the Windows title bar.
- Updated the README and project website for the new release.

## 0.1.1 — Manual quarantine options

- Expanded manual cleanup matching to registered user profiles, Program Files x64/x86 and additional app-data roots.
- Added a reversible quarantine action alongside permanent deletion in the manual path review.
- Added quarantine and restore support for both files and folders.

## 0.1.0 — First Windows preview

- Added a WPF application targeting .NET 10 for Windows x64.
- Added a registry-based installed application inventory for HKLM and HKCU in 32-bit and 64-bit views.
- Added explicit confirmation before starting a registered uninstaller.
- Added limited exact product-folder and publisher/product application-data review and local quarantine/restore.
- Added SQLite history and quarantine metadata.
- Added safety path guards and sandboxed tests.
- Added English, Italian, Spanish and French desktop localization with a saved language choice.
- Added a static four-language project website.

The 0.2.0 release is an early Windows x64 preview distributed as an unsigned portable ZIP. It is not an installer and does not include an updater.
