#define MyAppName "DayZ-Map.ru Companion"
#define MyAppPublisher "DayZ-Map.ru"
#define MyAppExeName "DayZMapCompanion.exe"
#define TesseractInstaller "tesseract-ocr-w64-setup-5.5.3.20260724.exe"
#define TesseractInstallerSha256 "bee9e3434bd94fd65387d9be28cd467a41f61b1275383b55b0f59a1331270ae4"
#define TesseractEnglishDataSha256 "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"
#define TesseractRussianDataSha256 "e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225"
#define MyAppVersion GetEnv("DAYZ_COMPANION_VERSION")

#if MyAppVersion == ""
  #define MyAppVersion "0.1.0"
#endif

[Setup]
AppId={{7E8F9DF7-7C0A-4D93-9DA8-4B4C6D40F23F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=DayZ-Map-ru-Companion-Setup-{#MyAppVersion}
SetupIconFile=..\assets\dayz-map-companion.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter={#MyAppExeName}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "third_party\{#TesseractInstaller}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Hash: "{#TesseractInstallerSha256}"
Source: "third_party\eng.traineddata"; DestDir: "{tmp}"; Flags: deleteafterinstall; Hash: "{#TesseractEnglishDataSha256}"
Source: "third_party\rus.traineddata"; DestDir: "{tmp}"; Flags: deleteafterinstall; Hash: "{#TesseractRussianDataSha256}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{app}\{#MyAppExeName}"
Type: files; Name: "{app}\Crosslay.exe"

[Code]
procedure StopProcess(const ExeName: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM "' + ExeName + '" /T >nul 2>nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(750);
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM "' + ExeName + '" /T /F >nul 2>nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopRunningApp();
begin
  StopProcess('{#MyAppExeName}');
  StopProcess('Crosslay.exe');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

function IsTesseractInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Programs\Tesseract-OCR\tesseract.exe')) or
    FileExists(ExpandConstant('{autopf}\Tesseract-OCR\tesseract.exe'));
end;

[Run]
Filename: "{tmp}\{#TesseractInstaller}"; Parameters: "/S /D={localappdata}\Programs\Tesseract-OCR"; StatusMsg: "Установка Tesseract OCR…"; Flags: waituntilterminated shellexec; Check: not IsTesseractInstalled
Filename: "{cmd}"; Parameters: "/C copy /Y ""{tmp}\eng.traineddata"" ""{localappdata}\Programs\Tesseract-OCR\tessdata\eng.traineddata"" && copy /Y ""{tmp}\rus.traineddata"" ""{localappdata}\Programs\Tesseract-OCR\tessdata\rus.traineddata"""; StatusMsg: "Установка языков Tesseract OCR…"; Flags: runhidden waituntilterminated; Check: IsTesseractInstalled
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
