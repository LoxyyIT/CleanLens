# Contributing

Thanks for helping improve CleanLens. The project prioritizes safety, reversibility and clarity over broad cleanup coverage.

## Before opening a change

- Describe the user-visible behavior and its safety boundary.
- Keep Registry, service, task and personal-data changes read-only unless the change includes a reviewed recovery design.
- Never treat a name match by itself as proof that data belongs to an application.
- Keep tests isolated in temporary directories or dedicated test Registry locations.
- Do not run cleanup tests against installed applications or real user folders.
- Do not add telemetry, analytics or network upload behavior.

## Build and validation

    dotnet restore CleanLens.sln
    dotnet build CleanLens.sln -c Release
    dotnet test CleanLens.sln -c Release
    git diff --check

## Pull requests

Include the behavior tested, affected Windows surfaces, limitations and any recovery constraints. Avoid claiming support for a feature until its UI, backend, error handling, translations and tests are complete.
