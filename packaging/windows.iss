#ifndef AppVersion
  #define AppVersion "0.0.1"
#endif
[Setup]
AppId={{19CA7D99-C823-4C03-9D85-7DDD3737E37C}
AppName=Krosforge
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Krosforge
DefaultGroupName=Krosforge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\.artifacts
OutputBaseFilename=Krosforge-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
LicenseFile=..\LICENSE
CloseApplications=yes
CloseApplicationsFilter=Krosforge.exe
[Files]
Source: "..\.artifacts\app\Krosforge.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Krosforge"; Filename: "{app}\Krosforge.exe"; Parameters: "--installed"
[Run]
Filename: "{app}\Krosforge.exe"; Parameters: "--installed"; Description: "Abrir Krosforge"; Flags: nowait postinstall skipifsilent
