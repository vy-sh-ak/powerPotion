# Deploys the Release host + Revit add-in into the per-user installed layout.
# Close Revit 2026 first: it locks PowerPotion.Revit.dll while running.
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$hostOut = Join-Path $repo "src\PowerPotion.Host\bin\Release\net8.0-windows"
$revitOut = Join-Path $repo "src\PowerPotion.Revit\bin\Release\net8.0-windows"
$dest = Join-Path $env:LocalAppData "PowerPotion"
$revitDest = Join-Path $dest "Revit"

if (-not (Test-Path (Join-Path $hostOut "PowerPotion.Host.exe"))) {
    throw "Host output missing. Run: dotnet build src\PowerPotion.Host\PowerPotion.Host.csproj -c Release"
}
if (-not (Test-Path (Join-Path $revitOut "PowerPotion.Revit.dll"))) {
    throw "Add-in output missing. Run: dotnet build src\PowerPotion.Revit\PowerPotion.Revit.csproj -c Release"
}

New-Item -ItemType Directory -Force $dest, $revitDest | Out-Null

try {
    Copy-Item "$hostOut\*" $dest -Force -Recurse
    Copy-Item "$revitOut\PowerPotion.Revit.dll" $revitDest -Force
    Copy-Item "$revitOut\PowerPotion.Protocol.dll" $revitDest -Force
} catch {
    throw "Deploy failed (is Revit still running?): $($_.Exception.Message)"
}

$manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2026\PowerPotion.addin"
if (-not (Test-Path $manifest)) {
    throw "Revit add-in manifest missing: $manifest"
}

Write-Host "Host      -> $dest"
Write-Host "Add-in    -> $revitDest"
Write-Host "Manifest  -> $manifest"
Write-Host "Deploy OK. Reopen Revit 2026."
