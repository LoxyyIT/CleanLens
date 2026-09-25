# Roadmap

Roadmap entries are plans, not implemented features.

## Current source prototype

- Registry uninstall inventory for machine and current-user entries in both Registry views.
- Current-user AppX/MSIX inventory and package removal after confirmation, respecting Windows' non-removable marker.
- Registered uninstaller launch after command review, Authenticode verification and confirmation.
- Exact product-folder and publisher/product AppData and ProgramData scan, with read-only service, scheduled-task and startup matches.
- On-demand measured file-length totals across matched application install and data folders.
- Local install-monitor sessions with before/after app inventory and file system event review.
- Locally saved manual search roots that can be enabled, disabled or removed.
- Selected-folder quarantine move and restore to an unoccupied original path.
- Local SQLite operation and quarantine history.

## Next safety work

- Extend sandbox coverage for junctions, nested reparse points, races and blocked paths.
- Make quarantine metadata and recovery records transactional and resilient to interrupted operations.
- Expand desktop localization validation to cover new strings and runtime UI flows.

## Planned product work

- More complete application identity and duplicate-resolution rules.
- Carefully scoped Registry candidates with backups and tested restore.
- AppX package management across user profiles, where Windows permissions and package deployment support it.
- Separate personal-data discovery and explicit warnings, only after safe evidence and recovery are designed.
- Batch uninstall, history details, retention settings and safe permanent-quarantine removal.
- More complete installation snapshots, root coverage and saved-session comparison without invasive process hooking.
- System, light and dark themes; accessibility and DPI validation.
- Windows version matrix, signing and portable package automation.

System-artifact detection is read-only. Install Monitor covers only roots Windows allowed CleanLens to watch and reports event overflow. Disk measurement sums file lengths and does not claim allocated space. No planned capability should be advertised as available until its UI, backend, error handling, localization and tests are complete.
