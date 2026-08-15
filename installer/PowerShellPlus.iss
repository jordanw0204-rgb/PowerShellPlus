#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\release-native"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\build\installer"
#endif

[Setup]
AppId={{C427C96B-1497-4BAA-8F82-2E47D06D8C68}
AppName=PowerShellPlus
AppVersion={#MyAppVersion}
AppVerName=PowerShellPlus {#MyAppVersion}
AppPublisher=PowerShellPlus
AppPublisherURL=https://github.com/jordanw0204-rgb/PowerShellPlus
AppSupportURL=https://github.com/jordanw0204-rgb/PowerShellPlus/issues
AppUpdatesURL=https://github.com/jordanw0204-rgb/PowerShellPlus/releases/latest
DefaultDirName={localappdata}\Programs\PowerShellPlus
DefaultGroupName=PowerShellPlus
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#MyOutputDir}
OutputBaseFilename=PowerShellPlus-Setup-x64
SetupIconFile=..\native\PowerShellPlus.Native\Assets\PowerShellPlus.ico
UninstallDisplayIcon={app}\PowerShellPlus.exe
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=PowerShellPlus
VersionInfoDescription=PowerShellPlus Installer
VersionInfoProductName=PowerShellPlus
VersionInfoProductVersion={#MyAppVersion}

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PowerShellPlus"; Filename: "{app}\PowerShellPlus.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\PowerShellPlus"; Filename: "{app}\PowerShellPlus.exe"; WorkingDir: "{app}"; Check: ShouldCreateDesktopShortcut

[Run]
Filename: "{app}\PowerShellPlus.exe"; Description: "Launch PowerShellPlus"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\PowerShellPlus.exe"; WorkingDir: "{app}"; Flags: nowait skipifdoesntexist; Check: IsSilentUpdate

[Code]
function IsSilentUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:UPDATE|0}') = '1');
end;

function ShouldCreateDesktopShortcut: Boolean;
begin
  { The production installer and every updater refresh this stable shortcut. }
  { Isolated installer smoke tests opt out so they never touch the real desktop. }
  Result := ExpandConstant('{param:NODESKTOPSHORTCUT|0}') <> '1';
end;
