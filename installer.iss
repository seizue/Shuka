#define MyAppName "Shuka"
#ifndef MyAppVersion
  #define MyAppVersion "1.0"
#endif
#define MyAppPublisher "Shuka"
#define MyAppExeName "download-epub.bat"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Shuka
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=Shuka_Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; All root-level publish files (includes .NET runtime for self-contained app)
Source: "bin\publish\*";                                  DestDir: "{app}"; Flags: ignoreversion
Source: "ShukaIcon.ico";                                  DestDir: "{app}"; Flags: ignoreversion
Source: "bin\publish\.playwright\*";                      DestDir: "{app}\.playwright"; Flags: ignoreversion recursesubdirs
Source: "bin\publish\runtimes\*";                         DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "download-epub.bat";                              DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\ShukaIcon.ico"
Name: "{userdesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\ShukaIcon.ico"; Tasks: desktopicon

[Run]
; Install Playwright Chromium browser (needed for Cloudflare-protected sites)
Filename: "{app}\Shuka.exe"; Parameters: "playwright install chromium"; Description: "Installing browser for Cloudflare bypass..."; StatusMsg: "Please wait, installing browser components (Cloudflare bypass)..."; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent shellexec
