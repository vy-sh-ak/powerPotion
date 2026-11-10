# P0 smoke check: build the host, run the standalone Vulkan spike, and verify the capture.
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repo "src\PowerPotion.Host\PowerPotion.Host.csproj"
$exe = Join-Path $repo "src\PowerPotion.Host\bin\Release\net8.0-windows\PowerPotion.Host.exe"
$shot = Join-Path $env:TEMP "powerpotion_p0.png"

dotnet build $hostProject -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

if (Test-Path $shot) { Remove-Item $shot -Force }

$out = Join-Path $env:TEMP "powerpotion_p0_stdout.txt"
$err = Join-Path $env:TEMP "powerpotion_p0_stderr.txt"
$process = Start-Process -FilePath $exe `
    -ArgumentList "--p0", "--shot", $shot, "--seconds", "8", "--resize" `
    -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $out -RedirectStandardError $err

Get-Content $out
if ((Get-Item $err).Length -gt 0) { Get-Content $err }

if ($process.ExitCode -ne 0) { throw "P0 failed with exit code $($process.ExitCode)" }
if (-not (Test-Path $shot)) { throw "P0 capture missing: $shot" }

Write-Host "P0 PASSED: $shot"
