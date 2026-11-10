; PowerPotion — installer/uninstaller for the standalone Vulkan Revit renderer.
; Builds a single PowerPotionSetup.exe that installs the Revit add-in + renderer host
; per-user (no admin required) and registers a clean uninstaller that removes all traces.
;
; Build:  ISCC.exe PowerPotion.iss  (Inno Setup 6+)
; Output: installer\Output\PowerPotionSetup.exe

#define PowerPotionName "PowerPotion"
#define PowerPotionVersion "1.0.0"
#define PowerPotionPublisher "PowerPotion (personalized renderer)"

[Setup]
AppId={{7F5A1C92-3B4D-4E8F-9A60-2C7D1B55E4A3}
AppName={#PowerPotionName}
AppVersion={#PowerPotionVersion}
AppPublisher={#PowerPotionPublisher}
DefaultDirName={localappdata}\PowerPotion
DefaultGroupName={#PowerPotionName}
OutputDir=Output
OutputBaseFilename=PowerPotionSetup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.txt
InfoBeforeFile=..\README.md
UninstallDisplayName={#PowerPotionName} for Revit
UninstallDisplayIcon={app}\PowerPotion.Host.exe
SetupIconFile=..\assets\branding\powerpotion.ico
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizard-small.bmp

[Messages]
WelcomeLabel2=This will install [name/ver] — a personalized real-time rendering companion for Autodesk Revit with its own standalone Vulkan renderer.%n%nPrerequisites:%n• Autodesk Revit 2026%n• Windows 10/11 x64 with a Vulkan 1.3-capable GPU%n%nNo separate rendering engine is required. The installer places the add-in in your per-user Revit Addins folder. No administrator rights needed.

[Files]
; renderer host + settings
Source: "..\src\PowerPotion.Host\bin\Release\net8.0-windows\PowerPotion.Host.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\PowerPotion.Host\bin\Release\net8.0-windows\PowerPotion.Host.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\PowerPotion.Host\bin\Release\net8.0-windows\PowerPotion.Host.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\PowerPotion.Host\bin\Release\net8.0-windows\*.dll"; DestDir: "{app}"; Flags: ignoreversion
; Revit add-in + manifest
Source: "..\src\PowerPotion.Revit\bin\Release\net8.0-windows\PowerPotion.Revit.dll"; DestDir: "{app}\Revit"; Flags: ignoreversion
Source: "..\src\PowerPotion.Revit\bin\Release\net8.0-windows\PowerPotion.Protocol.dll"; DestDir: "{app}\Revit"; Flags: ignoreversion
Source: "..\src\PowerPotion.Revit\bin\Release\net8.0-windows\PowerPotion.addin"; DestDir: "{code:RevitAddinsDir}\2026"; Flags: ignoreversion

[Dirs]
Name: "{app}"
Name: "{code:RevitAddinsDir}\2026"

[Registry]
; per-user file association-free keys (uninstalled automatically by Inno):
Root: HKCU; Subkey: "Software\PowerPotion"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\PowerPotion"; ValueType: string; ValueName: "Version"; ValueData: "{#PowerPotionVersion}"; Flags: uninsdeletevalue

[Code]
function RevitAddinsDir(Param: string): string;
begin
  Result := ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins';
end;

// write the .addin manifest at install time with the absolute assembly path
procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, AddinPath: string;
  AddinXml: AnsiString;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');
    AddinPath := RevitAddinsDir('') + '\2026\PowerPotion.addin';
    AddinXml := '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
      '<RevitAddIns>' + #13#10 +
      '  <AddIn Type="Application">' + #13#10 +
      '    <Name>PowerPotion</Name>' + #13#10 +
      '    <Assembly>' + AppDir + '\Revit\PowerPotion.Revit.dll</Assembly>' + #13#10 +
      '    <AddInId>7F5A1C92-3B4D-4E8F-9A60-2C7D1B55E4A3</AddInId>' + #13#10 +
      '    <FullClassName>PowerPotion.Revit.PowerPotionApp</FullClassName>' + #13#10 +
      '    <VendorId>PWPT</VendorId>' + #13#10 +
      '    <VendorDescription>PowerPotion (standalone Vulkan renderer)</VendorDescription>' + #13#10 +
      '  </AddIn>' + #13#10 +
      '</RevitAddIns>' + #13#10;
    SaveStringToFile(AddinPath, AddinXml, False);
  end
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

[UninstallDelete]
; remove all runtime traces
Type: filesandordirs; Name: "{localappdata}\PowerPotion"
Type: filesandordirs; Name: "{userappdata}\PowerPotion"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\PowerPotion.addin"
Type: filesandordirs; Name: "{userdocs}\PowerPotion"

[Run]
Filename: "{app}\PowerPotion.Host.exe"; Parameters: "--locate"; Description: "Verify the renderer host starts"; Flags: runhidden skipifsilent
Filename: "https://github.com/"; Description: "Open the documentation"; Flags: postinstall shellexec unchecked

[Icons]
Name: "{group}\PowerPotion"; Filename: "{app}\PowerPotion.Host.exe"; Comment: "PowerPotion — live renderer for Revit"

[UninstallRun]
; ensure the renderer host is not running during uninstall
Filename: "taskkill"; Parameters: "/IM PowerPotion.Host.exe /F"; Flags: runhidden; RunOnceId: "KillHost"
