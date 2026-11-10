# P1 stress check: build, run the standalone Vulkan renderer through resize/vsync/HDR/minimize
# stress, and verify the frame loop + validation layer.
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repo "src\PowerPotion.Host\PowerPotion.Host.csproj"
$exe = Join-Path $repo "src\PowerPotion.Host\bin\Release\net8.0-windows\PowerPotion.Host.exe"
$shot = Join-Path $env:TEMP "powerpotion_p1_stress.png"

dotnet build $hostProject -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

# Use the Khronos validation layer if a local copy is available (e.g. the Steam CEF copy).
$steamLayer = "C:\Program Files (x86)\Steam\bin\cef\cef.win64\VkLayer_khronos_validation.dll"
$layerDir = Join-Path $env:TEMP "powerpotion_vk_layer"
if (Test-Path $steamLayer) {
    New-Item -ItemType Directory -Force $layerDir | Out-Null
    $escaped = $steamLayer.Replace("\", "\\")
    @"
{
  "file_format_version": "1.1.2",
  "layer": {
    "name": "VK_LAYER_KHRONOS_validation",
    "type": "GLOBAL",
    "library_path": "$escaped",
    "api_version": "1.3.0",
    "implementation_version": "1",
    "description": "Khronos Validation Layer (local copy)",
    "functions": {
      "vkGetInstanceProcAddr": "vkGetInstanceProcAddr",
      "vkGetDeviceProcAddr": "vkGetDeviceProcAddr"
    }
  }
}
"@ | Set-Content -Path (Join-Path $layerDir "VkLayer_khronos_validation.json") -Encoding UTF8

    $env:POWERPOTION_VK_VALIDATION = "1"
    $env:VK_LAYER_PATH = $layerDir
    $env:VK_INSTANCE_LAYERS = "VK_LAYER_KHRONOS_validation"
    Write-Host "Validation layer: $steamLayer"
} else {
    Remove-Item Env:POWERPOTION_VK_VALIDATION -ErrorAction SilentlyContinue
    Remove-Item Env:VK_LAYER_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:VK_INSTANCE_LAYERS -ErrorAction SilentlyContinue
    Write-Host "Validation layer not found; running without it."
}

if (Test-Path $shot) { Remove-Item $shot -Force }

$out = Join-Path $env:TEMP "powerpotion_p1_stdout.txt"
$err = Join-Path $env:TEMP "powerpotion_p1_stderr.txt"
$process = Start-Process -FilePath $exe `
    -ArgumentList "--p1", "--shot", $shot `
    -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $out -RedirectStandardError $err

Get-Content $out
if ((Get-Item $err).Length -gt 0) { Get-Content $err }

if ($process.ExitCode -ne 0) { throw "P1 failed with exit code $($process.ExitCode)" }
if (-not (Test-Path $shot)) { throw "P1 capture missing: $shot" }

$text = Get-Content $out -Raw
if ($text -match "VK ERROR") { throw "validation errors were reported; see $out" }

Write-Host "P1 PASSED: $shot"
