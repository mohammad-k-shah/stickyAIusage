; Sticky AI Usage - per-user Windows installer (Inno Setup 6.3 or later, 7 works too).
;
; Built by the Release workflow from the self-contained single-file publish:
;
;   iscc /DAppVersion=2.0.0 /DSourceDir=<publish folder> /O<output folder> installer\windows\setup.iss
;
; Installs for the current user only (no admin rights) to
; %LOCALAPPDATA%\Programs\Sticky AI Usage - the same folder scripts\install.ps1
; uses - with a Start Menu shortcut. winget runs it silently
; (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART).
;
; A running copy is never closed by force: Setup and Uninstall find it through
; the widget's single-instance mutex and ask to exit it from the tray. Restart
; Manager (CloseApplications) is off on purpose: antivirus programs keep the
; running exe open too, and Restart Manager then refuses to close anything and
; lists the antivirus as an app to close.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish"
#endif
#define AppName "Sticky AI Usage"
#define ExeName "StickyAIusage.exe"
; The default profile's single-instance mutex (App.xaml.cs: Local\StickyAIusage.SingleInstance).
; A test build passes a profile's mutex instead, so a widget already running
; on the test machine doesn't count as "running".
#ifndef AppMutexName
  #define AppMutexName "StickyAIusage.SingleInstance"
#endif
; The settings, cache and logs Uninstall offers to delete. A test build passes
; a scratch folder, so a real one can never be deleted by a test.
#ifndef DataDir
  #define DataDir "{localappdata}\StickyAIusage"
#endif
; "2.0.0-beta.2" -> "2.0.0": file version numbers are numbers only.
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)

[Setup]
; Never change the AppId: it is how Windows, winget and a newer Setup recognise an installed copy.
AppId={{FF38E1A6-407C-4A95-A184-D8CC911BD1CB}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mohammad Karimulla Shah
AppPublisherURL=https://github.com/mohammad-k-shah/stickyAIusage
AppSupportURL=https://github.com/mohammad-k-shah/stickyAIusage/issues
AppUpdatesURL=https://github.com/mohammad-k-shah/stickyAIusage/releases
PrivilegesRequired=lowest
DefaultDirName={userpf}\{#AppName}
DisableProgramGroupPage=yes
AppMutex={#AppMutexName}
SetupMutex=StickyAIusageSetup
CloseApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputBaseFilename=StickyAIusage-Setup-x64
SetupIconFile=..\..\src\StickyAIusage\Resources\app.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion

; How this copy was installed (written by CurStepChanged below); removed with the app.
[UninstallDelete]
Type: files; Name: "{app}\install-method"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"; Comment: "Your Claude usage limits, always in sight"

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ "Start with Windows" entries of this copy: StickyAIusage, and
  StickyAIusage-<profile> for developer profiles, whose command starts this
  exe. An entry for a copy somewhere else is left alone. }
procedure RemoveOwnStartEntries();
var
  Names: TArrayOfString;
  I: Integer;
  Name, Value, Exe: String;
begin
  Exe := Lowercase(ExpandConstant('{app}\{#ExeName}'));
  if not RegGetValueNames(HKEY_CURRENT_USER, RunKey, Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Name := Lowercase(Names[I]);
    if (Name = 'stickyaiusage') or (Pos('stickyaiusage-', Name) = 1) then
      if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, Names[I], Value) then
        if Pos(Exe, Lowercase(Value)) > 0 then
          RegDeleteValue(HKEY_CURRENT_USER, RunKey, Names[I]);
  end;
end;

{ How this copy was installed, for the app's update notice: "setup", or the
  word winget passes with /CHANNEL=winget (the winget manifest's custom switch).
  The app reads one known word and treats anything else as a download. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\install-method'), ExpandConstant('{param:CHANNEL|setup}'), False);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    RemoveOwnStartEntries();
  if CurUninstallStep = usPostUninstall then
  begin
    { Settings, cache and logs. A silent uninstall (winget) keeps them. }
    DataDir := ExpandConstant('{#DataDir}');
    if DirExists(DataDir) then
      if SuppressibleMsgBox('Also delete your Sticky AI Usage settings, cache and logs?' + #13#10#13#10 + DataDir,
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
