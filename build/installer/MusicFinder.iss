; MusicFinder installer (Inno Setup 6). Built by build\installer.ps1, which passes everything that changes:
;   ISCC /DAppVersion=<from Directory.Build.props> /DSourceDir=<published folder> /DOutputDir=<dist> MusicFinder.iss
; Nothing about the version or the folders is written in this file, so it cannot fall out of step with the build.
; The AppId must never change: Windows uses it to recognise an earlier install when upgrading (docs\installer.md).

#ifndef AppVersion
  #error AppVersion must be passed on the command line (/DAppVersion=...). Use build\installer.ps1.
#endif
#ifndef SourceDir
  #error SourceDir must be passed on the command line (/DSourceDir=...). Use build\installer.ps1.
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{D6BD24E8-EE8C-4630-9482-75B3E54A5CA2}
AppName=MusicFinder
AppVersion={#AppVersion}
AppVerName=MusicFinder {#AppVersion}
AppPublisher=Apostolis Kourtis
AppPublisherURL=https://github.com/kourtakos/MusicFinder
AppSupportURL=https://github.com/kourtakos/MusicFinder/issues
VersionInfoVersion={#AppVersion}.0
; Per-user: no administrator rights, nothing written outside the user's own folders. {autopf} is then %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
DefaultDirName={autopf}\MusicFinder
DefaultGroupName=MusicFinder
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\..\src\MusicFinder.App\Assets\app.ico
UninstallDisplayIcon={app}\MusicFinder.exe
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=MusicFinder-{#AppVersion}-Setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
; A portable copy leaves no uninstaller and no entry in Settings > Apps.
Uninstallable=not IsPortable
CreateUninstallRegKey=not IsPortable
; ; The installer is not signed yet (docs\installer.md). To sign it, define a SignTool in ISCC and enable:
; SignTool=mysign

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "greek"; MessagesFile: "Greek.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; Check: not IsPortable

[Files]
; Everything that was published, except the flag file: the flag decides portable mode, so only a portable copy gets it.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable.flag"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "portable.flag"; DestDir: "{app}"; Flags: ignoreversion; Check: IsPortable

[InstallDelete]
; An upgrade over an older copy: libraries the new version no longer has must not stay behind. Data\ (portable) and the data folder are never touched.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.deps.json"
Type: files; Name: "{app}\*.runtimeconfig.json"

[Icons]
Name: "{autoprograms}\MusicFinder"; Filename: "{app}\MusicFinder.exe"; Check: not IsPortable
Name: "{autodesktop}\MusicFinder"; Filename: "{app}\MusicFinder.exe"; Tasks: desktopicon; Check: not IsPortable

[Run]
Filename: "{app}\MusicFinder.exe"; Description: "{cm:LaunchProgram,MusicFinder}"; Flags: nowait postinstall skipifsilent

[Code]
var
  ModePage: TInputOptionWizardPage;
  PortableChosen: Boolean;

function IsPortable: Boolean;
begin
  Result := PortableChosen;
end;

function InitializeSetup: Boolean;
begin
  { Silent installs choose the mode on the command line: /MODE=portable (anything else installs). }
  PortableChosen := CompareText(ExpandConstant('{param:MODE|install}'), 'portable') = 0;
  Result := True;
end;

procedure InitializeWizard;
begin
  ModePage := CreateInputOptionPage(wpLicense,
    CustomMessage('ModeTitle'), CustomMessage('ModeSubtitle'),
    CustomMessage('ModeChoose'), True, False);
  ModePage.Add(CustomMessage('ModeInstall') + #13#10 +
    CustomMessage('ModeInstallInfo'));
  ModePage.Add(CustomMessage('ModePortable') + #13#10 +
    CustomMessage('ModePortableInfo'));
  ModePage.SelectedValueIndex := 0;
  if PortableChosen then ModePage.SelectedValueIndex := 1;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = ModePage.ID then PortableChosen := ModePage.SelectedValueIndex = 1;
  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { A portable copy is offered a folder in Documents rather than the program folder, until the user types one of their own. }
  if (CurPageID = wpSelectDir) and PortableChosen and
     (CompareText(WizardForm.DirEdit.Text, ExpandConstant('{autopf}\MusicFinder')) = 0) then
    WizardForm.DirEdit.Text := ExpandConstant('{userdocs}\MusicFinder');
  if (CurPageID = wpSelectDir) and (not PortableChosen) and
     (CompareText(WizardForm.DirEdit.Text, ExpandConstant('{userdocs}\MusicFinder')) = 0) then
    WizardForm.DirEdit.Text := ExpandConstant('{autopf}\MusicFinder');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  { No Start-menu or desktop choices for a portable copy. }
  if PortableChosen and (PageID = wpSelectTasks) then Result := True;
end;

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\MusicFinder');
  if not DirExists(DataDir) then Exit;

  { The library index, settings and backups are the user's. They are only deleted when the user says so; a silent uninstall keeps them
    unless /DELETEDATA=1 is given. The music files are never touched. }
  if UninstallSilent then
  begin
    if HasSwitch('/DELETEDATA=1') then DelTree(DataDir, True, True, True);
  end
  else if MsgBox(CustomMessage('DeleteDataQuestion') + #13#10#13#10 + DataDir + #13#10#13#10 +
                 CustomMessage('DeleteDataInfo'),
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;

[CustomMessages]
english.ModeTitle=How do you want to use MusicFinder?
english.ModeSubtitle=You can install it, or just copy it to a folder and run it from there.
english.ModeChoose=Choose one:
english.ModeInstall=Install it on this computer (recommended)
english.ModeInstallInfo=Adds MusicFinder to the Start menu and to Settings > Apps, so it can be removed later. Your library and settings are kept in your user profile.
english.ModePortable=Portable: copy it to a folder I choose
english.ModePortableInfo=Nothing is added to Windows. Everything MusicFinder stores stays in that folder, for example on a USB stick. Delete the folder to remove it.
english.DeleteDataQuestion=Also delete your MusicFinder library index, settings and backups?
english.DeleteDataInfo=Your music files are never touched. Choose No to keep them, for example to reinstall later.
greek.ModeTitle=Πώς θέλετε να χρησιμοποιήσετε το MusicFinder;
greek.ModeSubtitle=Μπορείτε να το εγκαταστήσετε ή απλώς να το αντιγράψετε σε έναν φάκελο και να το τρέξετε από εκεί.
greek.ModeChoose=Επιλέξτε ένα:
greek.ModeInstall=Εγκατάσταση σε αυτόν τον υπολογιστή (προτείνεται)
greek.ModeInstallInfo=Προσθέτει το MusicFinder στο μενού Έναρξη και στις Ρυθμίσεις > Εφαρμογές, ώστε να μπορεί να αφαιρεθεί αργότερα. Η βιβλιοθήκη και οι ρυθμίσεις σας φυλάσσονται στο προφίλ χρήστη σας.
greek.ModePortable=Φορητή χρήση: αντιγραφή σε φάκελο της επιλογής μου
greek.ModePortableInfo=Δεν προστίθεται τίποτα στα Windows. Όσα αποθηκεύει το MusicFinder μένουν σε αυτόν τον φάκελο, για παράδειγμα σε ένα USB stick. Διαγράψτε τον φάκελο για να το αφαιρέσετε.
greek.DeleteDataQuestion=Να διαγραφούν και το ευρετήριο της βιβλιοθήκης, οι ρυθμίσεις και τα αντίγραφα ασφαλείας του MusicFinder;
greek.DeleteDataInfo=Τα αρχεία μουσικής σας δεν θίγονται ποτέ. Επιλέξτε Όχι για να τα κρατήσετε, για παράδειγμα για μελλοντική επανεγκατάσταση.
