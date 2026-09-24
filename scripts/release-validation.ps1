$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet restore CleanLens.sln
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    dotnet build CleanLens.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    dotnet test CleanLens.sln -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
    & (Join-Path $root 'scripts/validate-localization.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Website localization validation failed.' }
    node --check docs/assets/js/site.js
    if ($LASTEXITCODE -ne 0) { throw 'Website JavaScript syntax validation failed.' }
    rg -n -i 'api[_-]?key\s*=|password\s*=|secret\s*=|BEGIN (RSA|OPENSSH|PRIVATE)' -g '!bin/**' -g '!obj/**' -g '!artifacts/**' $root
    if ($LASTEXITCODE -eq 0) { throw 'Potential secret material was found.' }
    if ($LASTEXITCODE -ne 1) { throw 'Secret scan could not complete.' }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
    git diff --cached --check
    if ($LASTEXITCODE -ne 0) { throw 'git diff --cached --check failed.' }
    Write-Output 'Release validation completed: restore, build, tests, website locales/assets, JavaScript syntax, secrets and whitespace.'
}
finally {
    Pop-Location
}
