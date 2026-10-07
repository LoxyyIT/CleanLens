# Security policy

## Supported versions

CleanLens is in early development. The current published preview is v0.4.0; security fixes are made on the default development branch.

## Reporting a vulnerability

Do not open a public issue for a vulnerability that could expose data or cause unsafe cleanup. Use GitHub's private vulnerability reporting for the repository when it is available. Include the affected version or commit, Windows version, reproduction steps and impact. Do not attach private user data.

## Security boundaries

- The application treats Registry values, paths, commands and file metadata as untrusted.
- The registered uninstaller command is shown to the user before it is started.
- Only an existing, fully qualified executable is started; MSI is resolved to the Windows system executable and quiet-only commands are refused.
- Third-party uninstaller Authenticode signatures are not verified in this prototype.
- The current candidate scanner requires a registry-confirmed uninstall and an exact product folder in a selected application-data root, or exact publisher/product directory components.
- Personal libraries, Windows directories, Program Files paths and reparse points are excluded from quarantine moves.
- Cleanup uses a reversible move to local quarantine. Permanent deletion of quarantined content is not implemented.
- Services, tasks, startup items, Registry entries, drivers, shared runtimes and browser extensions are not modified.
- The focused Defender scan covers only Downloads, Desktop, local temporary files and an optional user-selected folder. It uses a custom scan with remediation disabled and shows Defender's command output for review.
- CleanLens never removes or quarantines a Defender finding. The scan requires UAC elevation and does not replace full-device antivirus protection.
- CleanLens has no telemetry or report-upload feature. Defender's configured cloud protection and sample-submission settings remain in effect and may send samples to Microsoft.

These controls are not a guarantee against concurrent filesystem changes, malicious same-user processes, a defective third-party uninstaller, or every Windows path-resolution edge case. Review the registered command and every candidate path before proceeding.

## Safe testing

Tests must use temporary fixture directories and dedicated test databases. Never run destructive tests against real applications, user libraries, Program Files, Windows or the live software Registry.
