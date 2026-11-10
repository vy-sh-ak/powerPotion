# P7 guard: the product must not depend on Enscape in any form.
# Scans source/config files under src/ (excluding build artifacts) for runtime/assembly/path
# references. Historical comments and docs are allowed.
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$patterns = @(
    'Enscape\.RendererApi',
    'ENSCAPE_DIR',
    'POWERPOTION_ENSCAPE_DIR',
    'PowerPotion\.Host\.Enscape',
    'EnscapeLocator',
    '\bEngineBridge\b',
    'RendererSurface'
)

$files = Get-ChildItem -Path (Join-Path $repo "src") -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj)\\' -and
        $_.Extension -in '.cs', '.csproj', '.xaml', '.addin', '.manifest', '.props', '.targets'
    }

$hits = @()
foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw
    foreach ($pattern in $patterns) {
        if ($text -match $pattern) {
            $hits += [pscustomobject]@{ File = $file.FullName; Pattern = $pattern }
        }
    }
}

if ($hits.Count -gt 0) {
    Write-Host "P7 guard FAILED: Enscape references remain in product sources:"
    $hits | ForEach-Object { Write-Host ("  {0}  [{1}]" -f $_.File, $_.Pattern) }
    exit 1
}

Write-Host "P7 guard PASSED: no Enscape dependency references in src/."
exit 0
