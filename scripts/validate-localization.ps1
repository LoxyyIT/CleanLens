$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$html = Get-Content -LiteralPath (Join-Path $root 'docs/index.html') -Raw
$script = Get-Content -LiteralPath (Join-Path $root 'docs/assets/js/site.js') -Raw
$wpfXaml = Get-Content -LiteralPath (Join-Path $root 'src/CleanLens.App/MainWindow.xaml') -Raw
$wpfViewModel = Get-Content -LiteralPath (Join-Path $root 'src/CleanLens.App/MainViewModel.cs') -Raw
$wpfWindow = Get-Content -LiteralPath (Join-Path $root 'src/CleanLens.App/MainWindow.xaml.cs') -Raw
$catalog = Get-Content -LiteralPath (Join-Path $root 'src/CleanLens.Core/Localization/LocalizationCatalog.cs') -Raw
$required = [regex]::Matches($html, 'data-i18n(?:-aria)?="([A-Za-z0-9]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($language in @('en', 'it', 'es', 'fr')) {
    $next = switch ($language) { 'en' { 'it' } 'it' { 'es' } 'es' { 'fr' } 'fr' { 'languageNames' } }
    $start = $script.IndexOf(("  {0}: {{" -f $language), [StringComparison]::Ordinal)
    $finish = if ($language -eq 'fr') {
        $script.IndexOf('const languageNames', $start + 1, [StringComparison]::Ordinal)
    } else {
        $script.IndexOf(("  {0}:" -f $next), $start + 1, [StringComparison]::Ordinal)
    }
    if ($start -lt 0 -or $finish -lt 0) { throw "Could not find the $language translation dictionary." }
    $dictionary = $script.Substring($start, $finish - $start)
    $keys = [regex]::Matches($dictionary, '[,{]\s*([A-Za-z][A-Za-z0-9]*):') | ForEach-Object { $_.Groups[1].Value }
    $missing = $required | Where-Object { $_ -notin $keys }
    if ($missing) { throw "$language is missing website keys: $($missing -join ', ')" }
}
$catalogKeys = [regex]::Matches($catalog, '\["([A-Za-z][A-Za-z0-9]+)"\]\s*=') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$wpfKeys = [System.Collections.Generic.List[string]]::new()
foreach ($match in [regex]::Matches($wpfXaml, 'Texts\[([A-Za-z][A-Za-z0-9]+)\]')) { $wpfKeys.Add($match.Groups[1].Value) }
foreach ($source in @($wpfViewModel, $wpfWindow)) {
    foreach ($match in [regex]::Matches($source, 'Texts(?:\.Format)?\["?([A-Za-z][A-Za-z0-9]+)"?\]')) { $wpfKeys.Add($match.Groups[1].Value) }
    foreach ($match in [regex]::Matches($source, 'Texts\.Format\("([A-Za-z][A-Za-z0-9]+)"')) { $wpfKeys.Add($match.Groups[1].Value) }
}
$requiredConverterKeys = @('CategoryApplicationData', 'CategoryProgramData', 'ConfidenceLow', 'ConfidenceMedium', 'ConfidenceHigh', 'CandidatePublisherProductPath', 'CandidateProductFolderAfterUninstall')
$missingWpf = @($wpfKeys + $requiredConverterKeys | Sort-Object -Unique | Where-Object { $_ -notin $catalogKeys -and $_ -ne 'CurrentPageKey' })
if ($missingWpf) { throw "Desktop localization keys are missing: $($missingWpf -join ', ')" }
$assets = [regex]::Matches($html, '(?:src|href)="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -notmatch '^(?:https?://|#|mailto:|data:)' }
foreach ($asset in $assets) {
    $relativePath = $asset.Split('#')[0].Split('?')[0]
    if ($relativePath -and -not (Test-Path -LiteralPath (Join-Path $root ('docs/' + $relativePath.Replace('/', '\'))))) {
        throw "Missing relative website asset: $asset"
    }
}
Write-Output "Website and WPF locale keys passed: $($required.Count) website keys, 4 languages; desktop resources and relative assets resolved."
