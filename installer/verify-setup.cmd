@echo off
:: Verifies a PowerPotion installation by launching the host in engine-locate mode,
:: then checking the installed file layout.
setlocal

echo --- host self-locate ---
"%LOCALAPPDATA%\PowerPotion\PowerPotion.Host.exe" --locate
if errorlevel 1 (
    echo FAILED: the renderer host could not find the rendering engine.
    echo Make sure the rendering engine (4.7+) is installed, or set POWERPOTION_ENGINE_DIR.
    exit /b 1
)

echo --- installed layout ---
if not exist "%LOCALAPPDATA%\PowerPotion\PowerPotion.Host.exe" ( echo MISSING host & exit /b 1 )
if not exist "%LOCALAPPDATA%\PowerPotion\Revit\PowerPotion.Revit.dll" ( echo MISSING add-in & exit /b 1 )
if not exist "%APPDATA%\Autodesk\Revit\Addins\2026\PowerPotion.addin" ( echo MISSING manifest & exit /b 1 )

echo OK: host, add-in and manifest are installed.
echo Start Revit 2026 and use the PowerPotion tab to begin a live render.
exit /b 0
