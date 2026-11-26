function PowerShellExePath: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function AccountsFileArg: String;
begin
  Result := ' -AccountsFile "' + GetAccountsFile('') + '"';
end;

function DefaultPinsFileArg: String;
var F: String;
begin
  F := GetDefaultPinsFile('');
  if F = '' then
    Result := ''
  else
    Result := ' -DefaultPinsFile "' + F + '"';
end;

function RunServerPostinstallExec(const Phase, Mode, ExtraArgs: String; const FailExit: Integer): Boolean;
var
  Rc: Integer;
  Ps, Script, Params: String;
begin
  Result := True;
  Ps := PowerShellExePath;
  Script := ExpandConstant('{app}\tools\server-postinstall.ps1');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '"';
  if Mode <> '' then
    Params := Params + ' -InstallMode ' + Mode;
  Params := Params + ' -InstallPhase ' + Phase + ExtraArgs;
  Log('CHECKPOINT_POSTINSTALL_EXEC phase=' + Phase);
  if not ExecAndLogOutput(Ps, Params, '', SW_HIDE, ewWaitUntilTerminated, Rc, nil) then
  begin
    AbortPostinstallPhaseFailed(Phase, FailExit);
    Result := False;
    Exit;
  end;
  if Rc <> 0 then
  begin
    if Rc >= CHECKPOINT_EXIT_DOWNGRADE_BLOCKED then
      AbortPostinstallPhaseFailed(Phase, Rc)
    else
      AbortPostinstallPhaseFailed(Phase, FailExit);
    Result := False;
    Exit;
  end;
end;

procedure RunCheckpointServerPostinstallPhases;
begin
  if IsFirstInstall then
  begin
    if NeedWebView2Install then
      if not RunServerPostinstallExec('webview2', '', '', CHECKPOINT_EXIT_FAIL_WEBVIEW2) then Exit;
    if ReinstallKeptPgDataCheck then
      if not RunServerPostinstallExec('reinstall-kept-pg', '', '', CHECKPOINT_EXIT_FAIL_REINSTALL_KEPT_PG) then Exit;
    if FirstInstallDatabaseCheck then
      if not RunServerPostinstallExec('database', '', '', CHECKPOINT_EXIT_FAIL_DATABASE) then Exit;
    if not RunServerPostinstallExec('accounts', '', AccountsFileArg, CHECKPOINT_EXIT_FAIL_ACCOUNTS_PHASE) then Exit;
    if not RunServerPostinstallExec('services', '', '', CHECKPOINT_EXIT_FAIL_SERVICES) then Exit;
    if not RunServerPostinstallExec('firewall', '', '', CHECKPOINT_EXIT_FAIL_FIREWALL) then Exit;
    if not RunServerPostinstallExec('backup', '', AccountsFileArg, CHECKPOINT_EXIT_FAIL_BACKUP) then Exit;
    if not RunServerPostinstallExec('start', '', '', CHECKPOINT_EXIT_FAIL_START) then Exit;
    Exit;
  end;

  if not NormalUpgradeInstallCheck then Exit;
  if not UpgradeDefaultPinsReady then Exit;

  if UpgradeHasDefaultPinsFile then
    if not RunServerPostinstallExec('upgrade-pins', 'upgrade', DefaultPinsFileArg, CHECKPOINT_EXIT_FAIL_UPGRADE_PINS) then Exit;
  if not RunServerPostinstallExec('upgrade-stop', 'upgrade', '', CHECKPOINT_EXIT_FAIL_UPGRADE_STOP) then Exit;
  if not RunServerPostinstallExec('upgrade-backup', 'upgrade', '', CHECKPOINT_EXIT_FAIL_UPGRADE_BACKUP) then Exit;
  if not RunServerPostinstallExec('upgrade-start', 'upgrade', AccountsFileArg, CHECKPOINT_EXIT_FAIL_UPGRADE_START) then Exit;
end;