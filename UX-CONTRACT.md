# CleanLens desktop workflow contract

This document records shared desktop behavior. `DESIGN.md` describes the visual language. Safety boundaries remain defined by the code and `SECURITY.md`.

## Disk explorer

| User operation | Required behavior |
| --- | --- |
| Choose a scan target | Scan only after the user chooses a drive or folder and starts the scan. Read file and folder metadata only. |
| Review scan results | Browse the current folder or the largest items/files in the selected completed scan. Show the exact path and measured logical size. |
| Narrow the list | Search names and paths, apply a measured size range, and preserve both inputs while changing view or page. Load results from the temporary SQLite index; cancel superseded queries. |
| Open a folder | Navigate only to a scanned directory inside the chosen root. Back and Parent return within that root. |
| Select paths | Selection enables analysis or eligible actions; it does not itself alter files. |
| Permanently delete selected items | Recheck exact selected paths and show an explicit, separate confirmation that names the item count, paths and irreversible consequence. |

Disk size filters use half-open byte ranges: under 100 MB, 100 MB to 1 GB, and over 1 GB. These thresholds use the same 1024-based units as CleanLens's existing size formatter. No selection means any size. Folder rows use their measured subtree size; a largest-files view contains files only.

## Shared interaction and localization rules

- Keep actions discoverable by keyboard and mouse, with visible labels and focus.
- Search can be cleared immediately and returns focus to its field. Replaced searches cannot overwrite the latest result.
- Present loading, partial-scan and empty results as distinct states. Incomplete totals remain marked as estimates or lower bounds where applicable.
- Localize every new UI string in English, Italian, Spanish and French through `LocalizationCatalog`. Use its selected culture for visible numeric formatting.
- Keep Windows services, scheduled tasks, startup entries and Registry matches read-only unless a separately reviewed recovery workflow is implemented.
