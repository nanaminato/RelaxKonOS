; Build-time values are supplied by New-RelaxKonOSClientInnoSetup.ps1.
#ifndef AppVersion
  #error AppVersion is required.
#endif
#ifndef FileVersion
  #error FileVersion is required.
#endif
#ifndef PublishDirectory
  #error PublishDirectory is required.
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required.
#endif
#ifndef OutputFileName
  #error OutputFileName is required.
#endif
#ifndef IconFile
  #error IconFile is required.
#endif
#ifndef Architecture
  #error Architecture is required.
#endif

#define AppName "RelaxKonOS Client"
#define AppPublisher "RelaxKon"
#define AppExeName "RelaxKonOS.Client.Desktop.exe"

[Setup]
AppId={{B575311B-F7D4-4E02-A378-D2904919C44F}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#FileVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\RelaxKon\RelaxKonOS Client
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputFileName}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
