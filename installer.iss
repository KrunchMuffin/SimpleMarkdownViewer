; Inno Setup Script for Simple Markdown Viewer
; Download Inno Setup from: https://jrsoftware.org/isinfo.php

#define MyAppName "Simple Markdown Viewer"
#define MyAppVersion "1.0.3"
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
Name: "fileassoc_common"; Description: "Common extensions (.md, .markdown)"; GroupDescription: "File associations:"
Name: "fileassoc_extended"; Description: "Extended extensions (.mdown, .mkd, .mkdn, .mdwn, .mdtxt, .mdtext)"; GroupDescription: "File associations:"
Name: "fileassoc_special"; Description: "Specialized extensions (.mdx, .rmd)"; GroupDescription: "File associations:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register application with friendly name (always register capabilities for Open With)
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer"; ValueType: string; ValueName: ""; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A lightweight markdown viewer"
Root: HKLM; Subkey: "SOFTWARE\RegisteredApplications"; ValueType: string; ValueName: "SimpleMarkdownViewer"; ValueData: "SOFTWARE\SimpleMarkdownViewer\Capabilities"; Flags: uninsdeletevalue

; Capabilities - Common extensions
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_common
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".markdown"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_common

; Capabilities - Extended extensions
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdown"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_extended
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkd"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_extended
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkdn"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_extended
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdwn"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_extended
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdtxt"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_extended
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdtext"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_extended

; Capabilities - Specialized extensions
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdx"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_special
Root: HKLM; Subkey: "SOFTWARE\SimpleMarkdownViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".rmd"; ValueData: "SimpleMarkdownViewer.md"; Tasks: fileassoc_special

; App Paths for friendly name in Open With dialog
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SimpleMarkdownViewer.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SimpleMarkdownViewer.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

; ProgID registration (needed if any file association is selected)
Root: HKCR; Subkey: "SimpleMarkdownViewer.md"; ValueType: string; ValueName: ""; ValueData: "Markdown File"; Flags: uninsdeletekey; Tasks: fileassoc_common or fileassoc_extended or fileassoc_special
Root: HKCR; Subkey: "SimpleMarkdownViewer.md"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "{#MyAppName}"; Tasks: fileassoc_common or fileassoc_extended or fileassoc_special
Root: HKCR; Subkey: "SimpleMarkdownViewer.md\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_common or fileassoc_extended or fileassoc_special
Root: HKCR; Subkey: "SimpleMarkdownViewer.md\shell"; ValueType: string; ValueName: ""; ValueData: "open"; Tasks: fileassoc_common or fileassoc_extended or fileassoc_special
Root: HKCR; Subkey: "SimpleMarkdownViewer.md\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Tasks: fileassoc_common or fileassoc_extended or fileassoc_special
Root: HKCR; Subkey: "SimpleMarkdownViewer.md\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_common or fileassoc_extended or fileassoc_special

; Common extensions - default association
Root: HKCR; Subkey: ".md"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_common
Root: HKCR; Subkey: ".markdown"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_common

; Extended extensions - default association
Root: HKCR; Subkey: ".mdown"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mkd"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mkdn"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mdwn"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mdtxt"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mdtext"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_extended

; Specialized extensions - default association
Root: HKCR; Subkey: ".mdx"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_special
Root: HKCR; Subkey: ".rmd"; ValueType: string; ValueName: ""; ValueData: "SimpleMarkdownViewer.md"; Flags: uninsdeletevalue; Tasks: fileassoc_special

; OpenWithProgids - Common extensions
Root: HKCR; Subkey: ".md\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_common
Root: HKCR; Subkey: ".markdown\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_common

; OpenWithProgids - Extended extensions
Root: HKCR; Subkey: ".mdown\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mkd\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mkdn\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mdwn\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mdtxt\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_extended
Root: HKCR; Subkey: ".mdtext\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_extended

; OpenWithProgids - Specialized extensions
Root: HKCR; Subkey: ".mdx\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_special
Root: HKCR; Subkey: ".rmd\OpenWithProgids"; ValueType: string; ValueName: "SimpleMarkdownViewer.md"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_special

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
