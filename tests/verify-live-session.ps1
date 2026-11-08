param(
    [string]$LogDir = (Join-Path $env:LOCALAPPDATA 'PowerPotion\Logs'),
    [int]$MinimumCameraUpdates = 60,
    [switch]$RequireLiveEdit,
    [switch]$RequireNavigation
)

$hostLogPath  = Join-Path $LogDir 'host.log'
$revitLogPath = Join-Path $LogDir 'revit-sync.log'
$engineLogPath = Join-Path $LogDir 'engine.log'

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $checks.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
}

if (-not (Test-Path $hostLogPath)) {
    Write-Host "host.log not found: $hostLogPath" -ForegroundColor Red
    exit 2
}

$hostText = Get-Content $hostLogPath -Raw
$revitText = if (Test-Path $revitLogPath) { Get-Content $revitLogPath -Raw } else { '' }
$engineTail = if (Test-Path $engineLogPath) { (Get-Content $engineLogPath -Tail 400) -join "`n" } else { '' }

# Only the most recent session counts (both files are append-only).
$marker = $hostText.LastIndexOf('---- host start ----')
if ($marker -ge 0) { $hostText = $hostText.Substring($marker) }
$marker = $revitText.LastIndexOf('Live sync started')
if ($marker -ge 0) { $revitText = $revitText.Substring($marker) }

$cameraUpdates = 0
foreach ($m in [regex]::Matches($hostText, 'camera stream: (\d+) updates')) {
    $cameraUpdates = [Math]::Max($cameraUpdates, [int]$m.Groups[1].Value)
}

$exports = ([regex]::Matches($revitText, 'Exported \d+ models')).Count
$worldSent = ([regex]::Matches($revitText, 'World sent')).Count
$worldApplies = ([regex]::Matches($hostText, 'OnWorld apply:')).Count
$worldCommits = ([regex]::Matches($hostText, 'World update finished: 0')).Count

Check 'renderer host started'                    ($hostText -match '---- host start ----')
Check 'single embedded render window'            ($hostText -match 'Embedded engine window')
Check 'scene received from Revit'                ($worldApplies -ge 1) "world applies: $worldApplies"
Check 'scene committed by engine'                ($worldCommits -ge 1) "engine commits: $worldCommits"
Check 'camera streaming (>= 30 Hz updates)'      ($cameraUpdates -ge $MinimumCameraUpdates) "camera updates: $cameraUpdates"
Check 'initial export sent by Revit'             ($exports -ge 1 -and $worldSent -ge 1) "exports: $exports, world sent: $worldSent"
if ($RequireLiveEdit) {
    Check 'live edit triggered a second export'  ($exports -ge 2) "exports: $exports"
    Check 'DocumentChanged queued an export'     ($revitText -match 'document changed -> export queued')
}
if ($RequireNavigation) {
    Check 'free navigation detached from Revit' ($hostText -match 'nav ui: follow=False')
    Check 'keyboard navigation input seen'       ($hostText -match 'nav input: keyboard')
    Check 'mouse look input seen'               ($hostText -match 'nav input: mouse look')
    Check 'mouse wheel input seen'              ($hostText -match 'nav input: mouse wheel')
}
Check 'no managed dispatcher crash'              ($hostText -notmatch 'Dispatcher unhandled|AppDomain unhandled')
Check 'no engine crash in tail'                  ($engineTail -notmatch 'Unhandled C\+\+ exception|\[FATAL\]')

$failed = 0
Write-Host ''
Write-Host 'PowerPotion live-session verification' -ForegroundColor Cyan
Write-Host ('logs: {0}' -f $LogDir)
Write-Host ''
foreach ($c in $checks) {
    if ($c.Ok) {
        Write-Host ('  [PASS] {0} {1}' -f $c.Name, $c.Detail) -ForegroundColor Green
    } else {
        $failed++
        Write-Host ('  [FAIL] {0} {1}' -f $c.Name, $c.Detail) -ForegroundColor Red
    }
}
Write-Host ''
if ($failed -eq 0) {
    Write-Host 'RESULT: PASS' -ForegroundColor Green
    exit 0
}
Write-Host ("RESULT: FAIL ({0} check(s))" -f $failed) -ForegroundColor Red
exit 1
