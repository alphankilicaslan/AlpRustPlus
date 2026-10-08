; =====================================================
; AlpRust+ Installer
; Inno Setup 6 script — builds a single-file Setup.exe
; =====================================================

#define MyAppName      "AlpRust+"
#define MyAppExeName   "RustPlusDesk.exe"
#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString("..\bin\Installer\publish\" + MyAppExeName)
#endif
#define MyAppPublisher "alphankilicaslan"
#define MyAppURL       "https://github.com/alphankilicaslan/AlpRust"
; Stable GUID — never change once published or the old uninstaller won't find it
#define MyAppId        "{{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; Smooth upgrades — keep user's previous install dir
UsePreviousAppDir=yes
UsePreviousGroup=yes
CreateUninstallRegKey=yes

; Output
OutputDir=..\bin\Installer
OutputBaseFilename=AlpRust+-Setup

; Compression
Compression=lzma2/max
SolidCompression=yes

; 64-bit only
ArchitecturesInstallIn64BitMode=x64

; UI
DisableProgramGroupPage=yes
SetupIconFile=..\Assets\rustplus-desktop-icon.ico
WizardImageFile=..\Assets\Images\installer.png
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
PrivilegesRequired=admin

; Min OS: Windows 10 (required for .NET 8 WPF)
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; All files and sub-folders from the self-contained publish directory
Source: "..\bin\Installer\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";       Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\runtimes"
Type: filesandordirs; Name: "{app}\Features"
Type: filesandordirs; Name: "{app}\Assets"
Type: filesandordirs; Name: "{app}\lang"
Type: filesandordirs; Name: "{app}\MapParser"

[Code]
// ---- Helpers ---------------------------------------------------------------

function IsWebView2Installed: Boolean;
var
  Ver: string;
begin
  Result := RegQueryStringValue(HKLM,
    'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv', Ver) and (Length(Ver) > 0) and (Ver <> '0.0.0.0');
  if not Result then
    Result := RegQueryStringValue(HKCU,
      'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Ver) and (Length(Ver) > 0) and (Ver <> '0.0.0.0');
end;

// Delete any stale uninstall keys left by older builds / Velopack
procedure DeleteOldUninstallKeys;
var
  Keys: array[0..7] of String;
  i: Integer;
begin
  Keys[0] := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\RustPlusDesk';
  Keys[1] := 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RustPlusDesk';
  Keys[2] := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Pronwan.RustPlusDesk';
  Keys[3] := 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Pronwan.RustPlusDesk';
  Keys[4] := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AlpRust+';
  Keys[5] := 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AlpRust+';
  Keys[6] := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}_is1';
  Keys[7] := 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}_is1';

  for i := 0 to 7 do
  begin
    RegDeleteKeyIncludingSubkeys(HKLM, Keys[i]);
    RegDeleteKeyIncludingSubkeys(HKCU, Keys[i]);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    DeleteOldUninstallKeys;
end;
