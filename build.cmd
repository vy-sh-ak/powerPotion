@echo off
setlocal
setlocal enabledelayedexpansion

:: ---------------------------------------------------------------------------
:: PowerPotion — full build script
:: Compiles the protocol library, renderer host and Revit add-in (Release),
:: then builds the installer with Inno Setup if available.
:: ---------------------------------------------------------------------------

set REPO=%~dp0
set REVIT_DIR=C:\Program Files\Autodesk\Revit 2026
set ENGINE_DIR=C:\Program Files\Enscape\RendererHost
set CONFIGURATION=Release

echo === PowerPotion build ===
echo Repo:       %REPO%
echo Revit:      %REVIT_DIR%
echo Engine:    %ENGINE_DIR%
echo.

if not exist "%REVIT_DIR%\RevitAPI.dll" (
    echo ERROR: RevitAPI.dll not found under "%REVIT_DIR%".
    echo        Edit REVIT_DIR in build.cmd to point at your Revit 2026 install.
    exit /b 1
)
if not exist "%ENGINE_DIR%\Enscape.RendererApi.dll" (
    echo ERROR: Enscape.RendererApi.dll not found under "%ENGINE_DIR%".
    echo        Install the rendering engine 4.7+ or edit ENGINE_DIR in build.cmd.
    exit /b 1
)

echo [1/4] Protocol library...
dotnet build "%REPO%src\PowerPotion.Protocol\PowerPotion.Protocol.csproj" -c %CONFIGURATION% -v q -nologo
if errorlevel 1 goto :fail

echo [2/4] Renderer host...
dotnet build "%REPO%src\PowerPotion.Host\PowerPotion.Host.csproj" -c %CONFIGURATION% -v q -nologo -p:EngineDir="%ENGINE_DIR%"
if errorlevel 1 goto :fail

echo [3/4] Revit add-in...
dotnet build "%REPO%src\PowerPotion.Revit\PowerPotion.Revit.csproj" -c %CONFIGURATION% -v q -nologo -p:RevitDir="%REVIT_DIR%"
if errorlevel 1 goto :fail

echo [4/4] Installer...
set ISCC=
for %%I in (ISCC.exe) do set ISCC=%%~$PATH:I
if "!ISCC!"=="" if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if "!ISCC!"=="" if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if "!ISCC!"=="" if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe

if "!ISCC!"=="" (
    echo Inno Setup not found — skipping installer packaging.
    echo Binaries are ready:
    echo   host:  %REPO%src\PowerPotion.Host\bin\%CONFIGURATION%\net8.0-windows\PowerPotion.Host.exe
    echo   addin: %REPO%src\PowerPotion.Revit\bin\%CONFIGURATION%\net8.0-windows\PowerPotion.Revit.dll
    echo To install without the installer: copy the host folder to %%LocalAppData%%\PowerPotion
    echo and the addin folder + PowerPotion.addin to %%AppData%%\Autodesk\Revit\Addins\2026\
) else (
    "!ISCC!" "%REPO%installer\PowerPotion.iss"
    if errorlevel 1 goto :fail
    echo Installer written: %REPO%installer\Output\PowerPotionSetup.exe
)

echo.
echo === BUILD OK ===
exit /b 0

:fail
echo.
echo === BUILD FAILED ===
exit /b 1
