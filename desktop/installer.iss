[Setup]
MinVersion=10.0.19041
AppId=TallyPager.Luporion
AppName=TallyPager
AppVersion={#AppVersion}
AppPublisher=Luporion
DefaultDirName={localappdata}\Programs\TallyPager
DefaultGroupName=TallyPager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#Artifacts}
OutputBaseFilename=TallyPager-Setup-Windows-x64
SolidCompression=yes
UninstallDisplayIcon={app}\TallyPager-Windows-x64.exe

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "{#Artifacts}\TallyPager-Windows-x64.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Artifacts}\TallyPager-Firmware-M5StickC-Plus2-{#AppVersion}.zip"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Artifacts}\TallyPager-Firmware-ESP8266-{#AppVersion}.zip"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Artifacts}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Artifacts}\esptool-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\TallyPager"; Filename: "{app}\TallyPager-Windows-x64.exe"

[Run]
Filename: "{app}\TallyPager-Windows-x64.exe"; Description: "TallyPager starten"; Flags: nowait postinstall skipifsilent
