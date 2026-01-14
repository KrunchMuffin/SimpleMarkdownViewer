; Inno Setup Script for Simple Markdown Viewer
; Download Inno Setup from: https://jrsoftware.org/isinfo.php

#define MyAppName "Simple Markdown Viewer"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Your Name"
#define MyAppURL "https://github.com/yourusername/SimpleMarkdownViewer"
#define MyAppExeName "SimpleMarkdownViewer.exe"

[Setup]
AppId={{8F3D2A1B-5C4E-4F6A-9B8D-7E2F1A3C5D4E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=LICENSE
OutputDir=installer
OutputBaseFilename=SimpleMarkdownViewer-Setup-{#MyAppVersion}
SetupIconFile=app-icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Associate with .md files"; GroupDescription: "File associations:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; File association for .md files
Root: HKCR; Subkey: ".md"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKCR; Subkey: "SimpleMarkdownViewer.md"; ValueType: string; ValueName: ""; ValueData: "Markdown File"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKCR; Subkey: "SimpleMarkdownViewer.md\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc
Root: HKCR; Subkey: "SimpleMarkdownViewer.md\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

; Also register .markdown extension
Root: HKCR; Subkey: ".markdown"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
