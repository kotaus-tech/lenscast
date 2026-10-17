#define AppVersion "0.1.0"
[Setup]
AppId={{F0BDA95A-5FB1-4A16-AD03-3C2574D5D67B}
AppName=Lenscast
AppVersion={#AppVersion}
DefaultDirName={autopf}\Lenscast
DefaultGroupName=Lenscast
UninstallDisplayIcon={app}\Lenscast.exe
OutputDir=..\artifacts
OutputBaseFilename=Lenscast-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Files]
Source: "..\artifacts\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Lenscast"; Filename: "{app}\Lenscast.exe"
[Run]
Filename: "{app}\Lenscast.exe"; Description: "Запустить Lenscast"; Flags: nowait postinstall skipifsilent runasoriginaluser
[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Lenscast"

[UninstallRun]
Filename: "{app}\adb\adb.exe"; Parameters: "-P 5038 kill-server"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "StopLenscastUsb"
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AdbPath: String;
begin
  Result := '';
  AdbPath := ExpandConstant('{app}\adb\adb.exe');
  if FileExists(AdbPath) then
    if not Exec(AdbPath, '-P 5038 kill-server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('Не удалось остановить компонент USB перед обновлением.');
end;
