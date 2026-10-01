; Packwright installer (Inno Setup 6). Built by the release workflow:
;   iscc /DAppVersion=1.0.1 /DSourceDir=<publish folder> /O<output folder> installer\Packwright.iss
; Installs for the current user by default (no administrator prompt), so a silent update is smooth. The "for all
; users" choice is offered when the installer is started as administrator.

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{B7C1F3A2-5D1E-4C3B-9A0E-6F5D2E8C7A41}
AppName=Packwright
AppVersion={#AppVersion}
AppVerName=Packwright {#AppVersion}
AppPublisher=itsmemac
AppPublisherURL=https://github.com/itsmemac/Packwright
AppSupportURL=https://github.com/itsmemac/Packwright/issues
AppUpdatesURL=https://github.com/itsmemac/Packwright/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Packwright
DefaultGroupName=Packwright
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=Packwright-Setup-v{#AppVersion}-windows-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Packwright.App\Assets\icon.ico
UninstallDisplayIcon={app}\Packwright.exe
LicenseFile=..\LICENSE
; Close a running Packwright before replacing its files, and start it again afterwards.
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=Packwright.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Packwright"; Filename: "{app}\Packwright.exe"
Name: "{autodesktop}\Packwright"; Filename: "{app}\Packwright.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Packwright.exe"; Description: "Start Packwright"; Flags: nowait postinstall skipifsilent
; A silent update (started by the app's "Install now") has no finish page, so start Packwright again here.
Filename: "{app}\Packwright.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent
