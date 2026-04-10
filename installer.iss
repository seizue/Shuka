#define MyAppName "Shuka"
#define MyAppVersion "1.0"
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
Source: "bin\publish\Shuka.exe";                          DestDir: "{app}"; Flags: ignoreversion
Source: "bin\publish\Shuka.dll";                          DestDir: "{app}"; Flags: ignoreversion
Source: "bin\publish\Shuka.deps.json";                    DestDir: "{app}"; Flags: ignoreversion
Source: "bin\publish\Shuka.runtimeconfig.json";           DestDir: "{app}"; Flags: ignoreversion
Source: "bin\publish\HtmlAgilityPack.dll";                DestDir: "{app}"; Flags: ignoreversion
Source: "bin\publish\System.Text.Encoding.CodePages.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\publish\runtimes\*";                         DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs
Source: "download-epub.bat";                              DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent shellexec
