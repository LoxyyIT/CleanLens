# Roadmap

Roadmap entries are plans, not implemented features.

## Current source prototype

- Registry uninstall inventory for machine and current-user entries in both Registry views.
- Registered uninstaller launch after showing the command and requesting confirmation.
- Limited exact product-folder and publisher/product AppData and ProgramData candidate scan.
- Selected-folder quarantine move and restore to an unoccupied original path.
- Local SQLite operation and quarantine history.

## Next safety work

- Extend sandbox coverage for junctions, nested reparse points, races and blocked paths.
- Make quarantine metadata and recovery records transactional and resilient to interrupted operations.
- Add a dedicated publisher/signature review and verification flow for the registered command.
- Add complete localization tests and EN/IT/ES/FR desktop strings before exposing additional UI.

## Planned product work

- More complete application identity and duplicate-resolution rules.
- Real measured directory sizes, progress reporting, cancellation and bounded concurrency.
- Read-only services, scheduled tasks and startup relationship inventory.
- Carefully scoped Registry candidates with backups and tested restore.
- MSIX/AppX inventory and official package-removal flow.
- Separate personal-data discovery and explicit warnings, only after safe evidence and recovery are designed.
- Batch uninstall, history details, retention settings and safe permanent-quarantine removal.
- Installation before/after snapshots without invasive process hooking.
- System, light and dark themes; accessibility and DPI validation.
- Windows version matrix, signing and portable package automation.

No planned capability should be advertised as available until its UI, backend, error handling, localization and tests are complete.
