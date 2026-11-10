# Deploys only the Release host. Stop the live render first (PowerPotion tab -> Stop),
# otherwise PowerPotion.Host.exe is locked.
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$hostOut = Join-Path $repo "src\PowerPotion.Host\bin\Release\net8.0-windows"
$dest = Join-Path $env:LocalAppData "PowerPotion"

if (-not (Test-Path (Join-Path $hostOut "PowerPotion.Host.exe"))) {
    throw "Host output missing. Run: dotnet build src\PowerPotion.Host\PowerPotion.Host.csproj -c Release"
}

New-Item -ItemType Directory -Force $dest | Out-Null
try {
    Copy-Item "$hostOut\*" $dest -Force -Recurse
} catch {
    throw "Host deploy failed (stop the live render first): $($_.Exception.Message)"
}

Write-Host "Host deployed -> $dest"
Write-Host "Start Live Render again from Revit."
