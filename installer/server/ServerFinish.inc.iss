const
  CM_SERVER_FINISH_IP_LABEL = 'inst_server_finish_ipLabel';
  CM_SERVER_FINISH_DHCP_NOTE = 'inst_server_finish_dhcpNote';

procedure CollectLanIPv4Addresses(var Ips: TArrayOfString; var Port: String);
var
  ScriptPath, OutFile: String;
  Lines: TArrayOfString;
  I, N: Integer;
  Line: String;
begin
  SetArrayLength(Ips, 0);
  Port := '5000';
  ScriptPath := ExpandConstant('{app}\tools\Get-ServerLanIPv4Addresses.ps1');
  if not FileExists(ScriptPath) then Exit;
  OutFile := ExpandConstant('{tmp}\finish-lan-ipv4.txt');
  if not Exec('cmd.exe', '/c powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -Format verbose > "' + OutFile + '" 2>nul', '', SW_HIDE, ewWaitUntilTerminated, I) then
    Exit;
  if not LoadStringsFromFile(OutFile, Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
    if Pos('IP=', Line) = 1 then
    begin
      N := GetArrayLength(Ips);
      SetArrayLength(Ips, N + 1);
      Ips[N] := Copy(Line, 4, MaxInt);
    end
    else if Pos('PORT=', Line) = 1 then
      Port := Copy(Line, 6, MaxInt);
  end;
end;

procedure UpdateServerFinishPage;
var
  Ips: TArrayOfString;
  Port, Base, CmIp, CmDhcp, Line: String;
  I: Integer;
begin
  CollectLanIPv4Addresses(Ips, Port);
  CmIp := ExpandConstant('{cm:' + CM_SERVER_FINISH_IP_LABEL + '}');
  CmDhcp := ExpandConstant('{cm:' + CM_SERVER_FINISH_DHCP_NOTE + '}');
  Base := WizardForm.FinishedLabel.Caption;
  if GetArrayLength(Ips) > 0 then
  begin
    for I := 0 to GetArrayLength(Ips) - 1 do
    begin
      Line := CmIp + ' http://' + Ips[I] + ':' + Port;
      Base := Base + #13#10 + Line;
      Log('server-finish-lan: ' + Ips[I] + ':' + Port);
    end;
  end;
  Base := Base + #13#10 + CmDhcp;
  WizardForm.FinishedLabel.Caption := Base;
end;