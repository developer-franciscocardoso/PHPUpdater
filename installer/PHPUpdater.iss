#ifndef ProjectRoot
  #define ProjectRoot SourcePath + "\.."
#endif
#ifndef PayloadManifestHash
  #error Build using build.ps1 so package checksums are embedded.
#endif

[Setup]
AppId={{FB21AC6E-F190-4A37-BF8C-1884DAD6E03C}
AppName=PHP and Composer Updater
AppVersion=1.0.5
AppPublisher=PHPUpdater
DefaultDirName={autopf}\PHPUpdater
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; Server 2016 is the minimum for the supported Visual C++ runtime.
MinVersion=10.0.14393
OutputDir={#ProjectRoot}\artifacts\dist
OutputBaseFilename=PHPUpdater-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
SetupMutex=Global\PHPUpdater.Setup
CloseApplications=no
UninstallDisplayName=PHP and Composer Updater
UninstallFilesDir={app}\uninstall
ChangesEnvironment=yes

[Files]
Source: "{#ProjectRoot}\artifacts\engine\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall
Source: "{#ProjectRoot}\artifacts\engine\*"; DestDir: "{app}\uninstall\engine"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall
Source: "{#ProjectRoot}\artifacts\engine\*"; DestDir: "{tmp}\engine"; Flags: dontcopy recursesubdirs createallsubdirs
Source: "{#ProjectRoot}\artifacts\payload\*"; DestDir: "{tmp}\payload"; Flags: dontcopy

[Code]
var
  SourcePage: TInputDirWizardPage;
  BranchPage, DefaultPage, BuildPage, ConfigPage: TInputOptionWizardPage;
  Branches: TStringList;
  CatalogFile, DiscoveryFile, WizardFile, EnginePath: String;
  InitialDefault: String;
  AskedSwitch, RuntimeRestart: Boolean;
  InstallFailed: Boolean;
  InstallError: String;

#include "Bootstrap.iss"

function Q(Value: String): String;
begin
  if Pos('"', Value) > 0 then
    RaiseException('Quotes are not allowed in installer options.');
  Result := '"' + Value + '"';
end;

function OfflineFlag(): String;
var
  I: Integer;
begin
  Result := 'false';
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/OFFLINE') = 0 then Result := 'true';
end;

function RunHelper(Command: String): String;
var
  Code: Integer;
  ErrorFile, ErrorText: String;
begin
  ErrorFile := ExpandConstant('{tmp}\helper-result');
  DeleteFile(ErrorFile + '.error');
  Result := '';
  if not Exec(EnginePath, Command + ' --out ' + Q(ErrorFile), '',
      SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Result := 'Could not start the PHPUpdater helper.';
    Exit;
  end;
  if Code = 0 then Exit;
  ErrorText := 'PHPUpdater failed. No new default was activated.';
  ErrorText := LoadHelperError(ErrorFile, ErrorText);
  Result := ErrorText;
end;

procedure RunOutput(Command, OutputFile: String);
var
  Code: Integer;
  ErrorText, Operation: String;
begin
  Operation := Copy(Command, 1, Pos(' ', Command + ' ') - 1);
  Log('Starting helper operation: ' + Operation);
  DeleteFile(OutputFile + '.error');
  if not Exec(EnginePath, Command + ' --out ' + Q(OutputFile), '',
      SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException('Could not start the PHPUpdater helper.');
  if Code <> 0 then
  begin
    ErrorText := 'Helper operation "' + Operation + '": ' +
      LoadHelperError(OutputFile, 'Inspection failed.');
    Log(ErrorText);
    RaiseException(ErrorText);
  end;
end;

function ContainsBranch(List, Branch: String): Boolean;
begin
  Result := Pos(',' + Branch + ',', ',' + List + ',') > 0;
end;

procedure RefreshDiscovery();
var
  I, DefaultIndex: Integer;
  Detected, Selected, DesiredDefault, Notice: String;
  Requested: TStringList;
begin
  RunOutput('discover --extra ' + Q(SourcePage.Values[0]), DiscoveryFile);
  RunOutput('wizard --catalog ' + Q(CatalogFile) +
    ' --discovery ' + Q(DiscoveryFile), WizardFile);
  InitialDefault := GetIniString('Wizard', 'Default', '', WizardFile);
  Detected := GetIniString('Wizard', 'Detected', '', WizardFile);
  Selected := ExpandConstant('{param:PHPBRANCHES|}');
  if Selected = '' then Selected := Detected;
  Requested := TStringList.Create;
  try
    Requested.CommaText := Selected;
    for I := 0 to Requested.Count - 1 do
      if Branches.IndexOf(Requested[I]) < 0 then
        RaiseException('Unsupported /PHPBRANCHES value: ' + Requested[I]);
  finally
    Requested.Free;
  end;
  DesiredDefault := ExpandConstant('{param:DEFAULTPHP|}');
  if (DesiredDefault <> '') and (Branches.IndexOf(DesiredDefault) < 0) then
    RaiseException('Unsupported /DEFAULTPHP value.');
  if DesiredDefault = '' then DesiredDefault := InitialDefault;
  DefaultIndex := Branches.IndexOf(DesiredDefault);
  if DefaultIndex < 0 then DefaultIndex := Branches.Count - 1;
  if (InitialDefault = '') and (Selected <> '') and
      (ExpandConstant('{param:DEFAULTPHP|}') = '') then
    for I := 0 to Branches.Count - 1 do
      if ContainsBranch(Selected, Branches[I]) then DefaultIndex := I;
  if (Selected = '') and not WizardSilent then
    Selected := Branches[DefaultIndex];
  if (Selected = '') and WizardSilent then
    RaiseException('Silent installation requires /PHPBRANCHES when no supported PHP is detected.');
  if WizardSilent and (InitialDefault <> '') and
      (Branches.IndexOf(InitialDefault) < 0) and
      (ExpandConstant('{param:DEFAULTPHP|}') = '') then
    RaiseException('Replacing an unsupported default requires /DEFAULTPHP.');
  if WizardSilent and not ContainsBranch(Selected, Branches[DefaultIndex]) then
    RaiseException('Include the default branch in /PHPBRANCHES or select /DEFAULTPHP explicitly.');
  for I := 0 to Branches.Count - 1 do
  begin
    BranchPage.Values[I] := ContainsBranch(Selected, Branches[I]);
    DefaultPage.Values[I] := I = DefaultIndex;
  end;
  Notice := GetIniString('Wizard', 'Notice', '', WizardFile);
  Notice := Notice + #13#10 + 'Detected branches: ' + Detected;
  Notice := Notice + #13#10 + 'Existing Composer: ' +
    GetIniString('Wizard', 'Composer', 'not detected', WizardFile);
  Notice := Notice + #13#10 + GetIniString('Wizard', 'Warnings', '', WizardFile);
  BranchPage.SubCaptionLabel.Caption := Notice;
end;

procedure InitializeWizard();
var
  I: Integer;
  Build: String;
begin
  WizardForm.DirEdit.Text := ExpandConstant('{autopf}\PHPUpdater');
  ExtractStartupFiles();
  EnginePath := ExpandConstant('{tmp}\engine\PHPUpdater.exe');
  CatalogFile := ExpandConstant('{tmp}\catalog.json');
  DiscoveryFile := ExpandConstant('{tmp}\discovery.json');
  WizardFile := ExpandConstant('{tmp}\wizard.ini');
  RunOutput('catalog --bundle ' + Q(ExpandConstant('{tmp}\payload')) +
    ' --offline ' + OfflineFlag(), CatalogFile);
  SourcePage := CreateInputDirPage(wpWelcome, 'Existing PHP installation',
    'Optional: select an additional PHP directory.',
    'PHP on PATH is detected automatically. Original files remain untouched.',
    False, '');
  SourcePage.Add('Additional PHP directory (optional):');
  RunOutput('discover --extra ""', DiscoveryFile);
  RunOutput('wizard --catalog ' + Q(CatalogFile) +
    ' --discovery ' + Q(DiscoveryFile), WizardFile);
  Branches := TStringList.Create;
  Branches.CommaText := GetIniString('Wizard', 'Branches', '', WizardFile);
  if Branches.Count = 0 then RaiseException('No supported PHP branches are available.');
  BranchPage := CreateInputOptionPage(SourcePage.ID, 'PHP releases',
    'Choose the branches to install or update.', '', False, False);
  DefaultPage := CreateInputOptionPage(BranchPage.ID, 'Default PHP',
    'Choose which branch the php and composer commands use.',
    'Other installed branches remain available as php82, php85, and so on.',
    True, False);
  BuildPage := CreateInputOptionPage(DefaultPage.ID, 'PHP build variant',
    'Preserve compatibility with existing extensions.',
    'Automatic preserves each existing TS/NTS variant and uses NTS for new branches.',
    True, False);
  BuildPage.Add('Automatic (recommended)');
  BuildPage.Add('Non Thread Safe (NTS)');
  BuildPage.Add('Thread Safe (TS)');
  Build := Uppercase(ExpandConstant('{param:PHPBUILD|}'));
  BuildPage.Values[0] := Build = '';
  BuildPage.Values[1] := Build = 'NTS';
  BuildPage.Values[2] := Build = 'TS';
  if (Build <> '') and (Build <> 'NTS') and (Build <> 'TS') then
    RaiseException('/PHPBUILD must be TS or NTS.');
  ConfigPage := CreateInputOptionPage(BuildPage.ID, 'PHP configuration',
    'Choose how settings are prepared for the selected branches.',
    'Migration preserves same-branch settings and uses new bundled extensions. ' +
    'Third-party DLLs must be compatible. Original installations remain intact.',
    True, False);
  ConfigPage.Add('Preserve existing settings (recommended)');
  ConfigPage.Add('Clean configuration: do not enable custom settings or extensions');
  Build := Lowercase(ExpandConstant('{param:PHPCONFIG|migrate}'));
  ConfigPage.Values[0] := Build = 'migrate';
  ConfigPage.Values[1] := Build = 'clean';
  if (Build <> 'migrate') and (Build <> 'clean') then
    RaiseException('/PHPCONFIG must be migrate or clean.');
  for I := 0 to Branches.Count - 1 do
  begin
    BranchPage.Add(GetIniString('Wizard', Branches[I], Branches[I], WizardFile));
    DefaultPage.Add('PHP ' + Branches[I]);
  end;
  RefreshDiscovery();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I, SelectedCount: Integer;
begin
  Result := True;
  if CurPageID = SourcePage.ID then RefreshDiscovery();
  if CurPageID = BranchPage.ID then
  begin
    if not WizardSilent and not AskedSwitch and (InitialDefault <> '') and
       (Branches.IndexOf(InitialDefault) >= 0) and
       (InitialDefault <> Branches[Branches.Count - 1]) then
    begin
      AskedSwitch := True;
      if MsgBox('A newer PHP branch (' + Branches[Branches.Count - 1] +
          ') is available. Install it and make it the default?' + #13#10 +
          'Your existing branch will remain available.', mbConfirmation,
          MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        BranchPage.Values[Branches.Count - 1] := True;
        DefaultPage.SelectedValueIndex := Branches.Count - 1;
      end;
    end;
    SelectedCount := 0;
    for I := 0 to Branches.Count - 1 do
      if BranchPage.Values[I] then SelectedCount := SelectedCount + 1;
    if SelectedCount = 0 then
    begin
      if not WizardSilent then
        MsgBox('Select at least one PHP branch.', mbError, MB_OK);
      Result := False;
    end;
  end;
  if CurPageID = DefaultPage.ID then
  begin
    I := DefaultPage.SelectedValueIndex;
    if (I < 0) or not BranchPage.Values[I] then
    begin
      if not WizardSilent then
        MsgBox('Choose a default branch selected for installation.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Runtime: String;
begin
  Result := RunHelper('preflight');
  if Result <> '' then Exit;
  ExtractTemporaryFiles('{tmp}\payload\*');
  Runtime := ExpandConstant('{tmp}\payload\vc_redist.x64.exe');
  if CompareText(GetSHA256OfFile(Runtime), '{#RuntimeHash}') <> 0 then
  begin
    Result := 'Microsoft runtime checksum mismatch.';
    Exit;
  end;
  if not Exec(Runtime, '/install /quiet /norestart', '', SW_HIDE,
      ewWaitUntilTerminated, Code) then
  begin
    Result := 'Could not install the required Microsoft runtime.';
    Exit;
  end;
  if (Code <> 0) and (Code <> 1638) and (Code <> 3010) then
    Result := 'Microsoft runtime installation failed: ' + IntToStr(Code);
  RuntimeRestart := Code = 3010;
end;

  function ConfigurationMode(): String;
  begin
    if ConfigPage.Values[1] then Result := 'clean'
    else Result := 'migrate';
  end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
  Selected, DefaultBranch, Build, ErrorText: String;
begin
  if CurStep <> ssPostInstall then Exit;
  InstallFailed := True;
  Selected := '';
  for I := 0 to Branches.Count - 1 do
    if BranchPage.Values[I] then
    begin
      if Selected <> '' then Selected := Selected + ',';
      Selected := Selected + Branches[I];
    end;
  DefaultBranch := Branches[DefaultPage.SelectedValueIndex];
  Build := '';
  if BuildPage.Values[1] then Build := 'NTS';
  if BuildPage.Values[2] then Build := 'TS';
  ErrorText := RunHelper('install --catalog ' + Q(CatalogFile) +
    ' --discovery ' + Q(DiscoveryFile) + ' --branches ' + Q(Selected) +
    ' --default ' + Q(DefaultBranch) + ' --build ' + Q(Build) +
    ' --configuration ' + ConfigurationMode() +
    ' --offline ' + OfflineFlag() + ' --bundle ' +
    Q(ExpandConstant('{tmp}\payload')));
  InstallError := ErrorText;
  InstallFailed := ErrorText <> '';
  if InstallFailed then
  begin
    Log('Managed installation failed: ' + InstallError);
    SuppressibleMsgBox(InstallError, mbError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode(): Integer;
begin
  Result := 0;
  if InstallFailed then Result := 1;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID <> wpFinished then Exit;
  if InstallFailed then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Installation did not complete';
    WizardForm.FinishedLabel.Caption := InstallError + #13#10 +
      'The previous default has been preserved. Resolve the error and run setup again.';
  end
  else
    WizardForm.FinishedLabel.Caption :=
      'PHP and Composer are ready. Reopen terminals to refresh PATH.' + #13#10 +
      'Original installations remain intact. Uninstall removes managed versions.';
end;

function NeedRestart(): Boolean;
begin
  Result := RuntimeRestart;
end;

function InitializeUninstall(): Boolean;
var
  FindRec: TFindRec;
  Source, Target: String;
begin
  Result := False;
  if CheckForMutexes('Global\PHPUpdater.Setup') then
  begin
    SuppressibleMsgBox('Another PHPUpdater operation is running.',
      mbError, MB_OK, IDOK);
    Exit;
  end;
  CreateMutex('Global\PHPUpdater.Setup');
  Source := ExpandConstant('{app}\uninstall\engine');
  Target := ExpandConstant('{tmp}\engine');
  ForceDirectories(Target);
  if FindFirst(Source + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
          if not FileCopy(Source + '\' + FindRec.Name,
              Target + '\' + FindRec.Name, False) then Exit;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  EnginePath := Target + '\PHPUpdater.exe';
  Result := True;
end;

#include "..\artifacts\engine-cleanup.iss"

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ErrorText: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    CleanupMaintenance();
    if not UninstallSilent then
      MsgBox('Original PHP and Composer installations remain unchanged.' + #13#10 +
        'Any modified or unrecognized files were retained. See ' +
        ExpandConstant('{commonappdata}\PHPUpdater\uninstall-report.txt'),
        mbInformation, MB_OK);
    Exit;
  end;
  if CurUninstallStep <> usUninstall then Exit;
  ErrorText := RunHelper('uninstall');
  if ErrorText <> '' then
  begin
    RaiseException(ErrorText);
  end;
end;
