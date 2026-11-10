# Reads the live-session logs and reports the P4 signals that matter:
# standalone renderer selection, camera mode, section planes, screen refraction.
$ErrorActionPreference = "Stop"

$logs = Join-Path $env:LocalAppData "PowerPotion\Logs"
$sync = Join-Path $logs "revit-sync.log"
$hostLog = Join-Path $logs "host.log"

if (-not (Test-Path $sync)) { throw "Missing $sync. Start PowerPotion from Revit first." }
if (-not (Test-Path $hostLog)) { throw "Missing $hostLog. Start PowerPotion from Revit first." }

$syncLines = Select-String -Path $sync -Pattern "launching host with renderer=|camera mode:|section planes:" |
    Select-Object -Last 20
$hostLines = Select-String -Path $hostLog -Pattern "Vulkan renderer ready|screen refraction:|renderer: section:" |
    Select-Object -Last 20

Write-Host "--- Revit side (revit-sync.log) ---"
$syncLines | ForEach-Object { Write-Host $_.Line }

Write-Host "--- Renderer side (host.log) ---"
$hostLines | ForEach-Object { Write-Host $_.Line }

$syncText = Get-Content $sync -Raw
$hostText = Get-Content $hostLog -Raw

$checks = [ordered]@{
    "standalone renderer launched" = $syncText -match "launching host with renderer='pp'"
    "camera mode reported"         = ($syncText -match "camera mode: (perspective|ortho|two-point)") -or
                                     ($hostText -match "renderer: camera mode: (perspective|ortho|two-point)")
    "section planes reported"      = $syncText -match "section planes: \d+"
    "Vulkan renderer ready"        = $hostText -match "Vulkan renderer ready"
    "screen refraction enabled"    = $hostText -match "screen refraction: enabled"
}

Write-Host "--- Checks ---"
$failed = 0
foreach ($pair in $checks.GetEnumerator()) {
    $state = if ($pair.Value) { "PASS" } else { "FAIL" }
    if (-not $pair.Value) { $failed++ }
    Write-Host ("{0}: {1}" -f $state, $pair.Key)
}

if ($failed -gt 0) {
    if (-not $checks["camera mode reported"]) {
        Write-Host "HINT: in Revit, toggle View Control Bar -> Orthographic (or Two-Point Perspective),"
        Write-Host "      then rerun this verifier. The initial perspective mode is only logged on a mode change"
        Write-Host "      in the currently deployed add-in."
    }
    throw "$failed live P4 check(s) failed."
}
Write-Host "P4 LIVE PASSED: standalone renderer, camera mode, section planes and screen refraction are active."
