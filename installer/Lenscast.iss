#define AppVersion "0.2.0"
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
Source: "Stop-LenscastUsb.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Lenscast"; Filename: "{app}\Lenscast.exe"
[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lenscast: поиск телефона"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lenscast: поиск телефона"" dir=in action=allow program=""{app}\Lenscast.exe"" protocol=UDP remoteport=5353 profile=private remoteip=LocalSubnet enable=yes"; Flags: runhidden waituntilterminated
Filename: "{app}\Lenscast.exe"; Description: "Запустить Lenscast"; Flags: nowait postinstall skipifsilent runasoriginaluser
[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Lenscast"

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Stop-LenscastUsb.ps1"" -AdbPath ""{app}\adb\adb.exe"""; Flags: runhidden waituntilterminated; RunOnceId: "StopLenscastUsb"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lenscast: поиск телефона"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLenscastDiscoveryRule"
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AdbPath, Params: String;
begin
  Result := '';
  AdbPath := ExpandConstant('{app}\adb\adb.exe');
  if FileExists(AdbPath) then begin
    ExtractTemporaryFile('Stop-LenscastUsb.ps1');
    Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{tmp}\Stop-LenscastUsb.ps1') + '" -AdbPath "' + AdbPath + '"';
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := 'Не удалось подготовить обновление. Закройте Lenscast и повторите установку.'
    else if ResultCode <> 0 then
      Result := 'Не удалось освободить компонент USB. Закройте Lenscast и повторите установку.';
  end;
end;
