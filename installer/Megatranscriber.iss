#define MyAppName "Мегатранскрибатор"
#define MyAppVersion "0.9.0-beta"
#define MyAppExeName "Megatranscriber.exe"

[Setup]
AppId={{87572D6F-0AD2-4DAB-A24A-97129F127ED3}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion=0.9.0.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Локальный транскрибатор аудио и видео
DefaultDirName={localappdata}\Programs\Megatranscriber
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=..\out\installer
OutputBaseFilename=Megatranscriber-Setup-0.9.0-beta
SetupIconFile=..\managed\PolinMegatranscriber.App\Assets\Megatranscriber-AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Files]
Source: "..\out\Megatranscriber-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent