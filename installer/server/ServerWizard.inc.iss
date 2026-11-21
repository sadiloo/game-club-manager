var
  Manager1User, Manager1Pin, Manager1Pin2: String;
  Manager2User, Manager2Pin, Manager2Pin2: String;
  OperatorUser, OperatorPin, OperatorPin2: String;
  SecondaryBackupDir: String;
  AccountsJsonPath: String;
  IsUpgradeInstall: Boolean;

procedure SaveAccountsJson;
var
  S: AnsiString;
begin
  S := '{' + #13#10 +
    '  "manager1User": "' + Manager1User + '",' + #13#10 +
    '  "manager1Pin": "' + Manager1Pin + '",' + #13#10 +
    '  "manager2User": "' + Manager2User + '",' + #13#10 +
    '  "manager2Pin": "' + Manager2Pin + '",' + #13#10 +
    '  "operatorUser": "' + OperatorUser + '",' + #13#10 +
    '  "operatorPin": "' + OperatorPin + '"' + #13#10 +
    '}';
  SaveStringToFile(AccountsJsonPath, S, False);
end;

function IsFirstInstall: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonappdata}\Checkpoint\config\server.json'));
end;

function IsUpgradeInstallCheck: Boolean;
begin
  Result := not IsFirstInstall;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and IsFirstInstall and (AccountsJsonPath <> '') then
    SaveAccountsJson;
end;

procedure InitializeWizard;
begin
  IsUpgradeInstall := not IsFirstInstall;
  AccountsJsonPath := ExpandConstant('{tmp}\checkpoint-accounts.json');
  if ExpandConstant('{param:ACCOUNTS|}') <> '' then
    AccountsJsonPath := ExpandConstant('{param:ACCOUNTS|}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if IsUpgradeInstall and (PageID >= wpSelectTasks) then
    Result := False;
end;