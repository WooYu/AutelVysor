#define MyAppName "Autel Vysor"
#ifndef MyAppVersion
#define MyAppVersion "0.10.3"
#endif
#define MyAppPublisher "Autel"
#define MyAppExeName "AutelVysor.exe"

[Setup]
AppId={{B30D44AF-A77D-4C39-BE1B-4314D4F1F6AA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Autel\Autel Vysor
DefaultGroupName=Autel Vysor
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=AutelVysor-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=Autel
VersionInfoDescription=Autel
VersionInfoProductName=Autel Vysor
VersionInfoProductVersion={#MyAppVersion}
UninstallDisplayName=Autel Vysor

[Files]
Source: "..\dist\AutelVysor\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Autel Vysor"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\Autel Vysor"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Autel Vysor"; Flags: nowait postinstall skipifsilent


