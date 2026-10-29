[Setup]
AppName=PHPUpdater Bootstrap Test
AppVersion=1.0
DefaultDirName={tmp}\unused
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
DisableStartupPrompt=yes
OutputDir={#ProjectRoot}\artifacts\tests
OutputBaseFilename=Bootstrap-Test
Compression=lzma2/fast

[Files]
Source: "{#ProjectRoot}\artifacts\engine\*"; DestDir: "{tmp}\engine"; Flags: dontcopy recursesubdirs createallsubdirs
Source: "{#ProjectRoot}\artifacts\payload\manifest.json"; DestDir: "{tmp}\payload"; Flags: dontcopy

[Code]
#include "..\installer\Bootstrap.iss"

function InitializeSetup(): Boolean;
var
  Report, Command, TestMessage: String;
  Code: Integer;
begin
  Report := ExpandConstant('{param:RESULT|}');
  Result := False;
  try
    ExtractStartupFiles();
    Command := 'catalog --offline true --bundle "' +
      ExpandConstant('{tmp}\payload') + '" --out "' +
      ExpandConstant('{tmp}\catalog.json') + '"';
    if not Exec(ExpandConstant('{tmp}\engine\PHPUpdater.exe'),
        Command, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      RaiseException('Extracted helper could not start.');
    if Code <> 0 then RaiseException('Offline catalog failed.');
    if not FileExists(ExpandConstant('{tmp}\catalog.json')) then
      RaiseException('Offline catalog was not created.');
    Command := 'discover --extra "" --out "' +
      ExpandConstant('{tmp}\discovery.json') + '"';
    if not Exec(ExpandConstant('{tmp}\engine\PHPUpdater.exe'),
        Command, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      RaiseException('Discovery helper could not start.');
    if Code <> 0 then
      RaiseException(LoadHelperError(
        ExpandConstant('{tmp}\discovery.json'), 'Discovery failed.'));
    Command := 'wizard --catalog "' +
      ExpandConstant('{tmp}\catalog.json') + '" --discovery "' +
      ExpandConstant('{tmp}\discovery.json') + '" --out "' +
      ExpandConstant('{tmp}\wizard.ini') + '"';
    if not Exec(ExpandConstant('{tmp}\engine\PHPUpdater.exe'),
        Command, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      RaiseException('Wizard data helper could not start.');
    if Code <> 0 then RaiseException('Wizard data failed.');
    if GetIniString('Wizard', 'Branches', '',
        ExpandConstant('{tmp}\wizard.ini')) = '' then
      RaiseException('Wizard has no PHP branches.');
    TestMessage := 'O nome do diret' + Chr($00F3) + 'rio ' +
      Chr($00E9) + ' inv' + Chr($00E1) + 'lido.';
    SaveStringToFile(ExpandConstant('{tmp}\encoding.error'),
      Utf8Encode(TestMessage), False);
    if LoadHelperError(ExpandConstant('{tmp}\encoding'), '') <>
        TestMessage then RaiseException('UTF-8 error decoding failed.');
    SaveStringToFile(Report, 'PASS', False);
  except
    SaveStringToFile(Report, 'FAIL: ' + GetExceptionMessage(), False);
  end;
end;
