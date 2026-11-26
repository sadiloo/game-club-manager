const
  CHECKPOINT_EXIT_FAIL_KIOSK_POSTINSTALL = 150;

procedure ExitProcess(uExitCode: UINT);
  external 'ExitProcess@kernel32.dll stdcall';
function SetEnvironmentVariable(lpName, lpValue: String): BOOL;
  external 'SetEnvironmentVariableW@kernel32 stdcall';

procedure RunKioskPostinstallPhase;
var
  Rc: Integer;
  Ps, Script, Params: String;
begin
  Ps := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Script := ExpandConstant('{pf}\Checkpoint\Guard\tools\kiosk-postinstall.ps1');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '"';
  Log('CHECKPOINT_KIOSK_POSTINSTALL_EXEC');
  if not ExecAndLogOutput(Ps, Params, '', SW_HIDE, ewWaitUntilTerminated, Rc, nil) then
  begin
    Log('CHECKPOINT_KIOSK_POSTINSTALL_ABORT exec_failed');
    if not WizardSilent then
      MsgBox('پیکربندی کیوسک ناموفق بود. جزئیات در فایل گزارش نصاب.', mbError, MB_OK);
    ExitProcess(CHECKPOINT_EXIT_FAIL_KIOSK_POSTINSTALL);
  end;
  if Rc <> 0 then
  begin
    Log('CHECKPOINT_KIOSK_POSTINSTALL_ABORT exit=' + IntToStr(Rc));
    if not WizardSilent then
      MsgBox('پیکربندی کیوسک ناموفق بود (کد ' + IntToStr(Rc) + ').', mbError, MB_OK);
    if (Rc >= 128) then
      ExitProcess(Rc)
    else
      ExitProcess(CHECKPOINT_EXIT_FAIL_KIOSK_POSTINSTALL);
  end;
end;

#if CheckpointInternalTestHooks == "1"
procedure ApplyKioskInternalTestHooks;
begin
  if ExpandConstant('{param:KIOSKPOSTINSTALLFAIL|}') <> '' then
  begin
    SetEnvironmentVariable('CHECKPOINT_INTERNAL_TEST_HOOKS', '1');
    SetEnvironmentVariable('CHECKPOINT_KIOSK_POSTINSTALL_FAIL', '1');
    Log('CHECKPOINT_KIOSK_INTERNAL_TEST_HOOK fail=1');
  end;
end;
#endif

procedure KioskCurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RunKioskPostinstallPhase;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
end;