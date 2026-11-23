const
  MAX_DEFAULT_PIN_PAGES = 256;

var
  Manager1Page, Manager2Page, OperatorPage: TInputQueryWizardPage;
  BackupDirPage: TInputDirWizardPage;
  BackupEmptyWarnLabel, BackupSameDriveWarnLabel: TNewStaticText;
  M1Err0, M1Err1, M1Err2, M2Err0, M2Err1, M2Err2, OpErr0, OpErr1, OpErr2: TNewStaticText;
  Manager1User, Manager1Pin, Manager1Pin2: String;
  Manager2User, Manager2Pin, Manager2Pin2: String;
  OperatorUser, OperatorPin, OperatorPin2: String;
  SecondaryBackupDir: String;
  AccountsJsonPath: String;
  SilentAccountsPath: String;
  SilentDefaultPinsPath: String;
  DefaultPinsJsonPath: String;
  DefaultPinUsernames: TArrayOfString;
  DefaultPinUserCount: Integer;
  DefaultPinPageIds: array[0..MAX_DEFAULT_PIN_PAGES - 1] of Integer;
  DefaultPinPages: array[0..MAX_DEFAULT_PIN_PAGES - 1] of TInputQueryWizardPage;
  DefaultPinErrPin, DefaultPinErrRepeat: array[0..MAX_DEFAULT_PIN_PAGES - 1] of TNewStaticText;
  DefaultPinStoredPins: TArrayOfString;

function IsFirstInstall: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonappdata}\Checkpoint\config\server.json'));
end;

function IsUpgradeInstallCheck: Boolean;
begin
  Result := not IsFirstInstall;
end;

function IsWebView2Installed: Boolean;
begin
  Result := FileExists(ExpandConstant('{reg:HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5},pv}'));
end;

function NeedWebView2Install: Boolean;
begin
  Result := IsFirstInstall and (not IsWebView2Installed);
end;

function UseSilentAccounts: Boolean;
begin
  Result := SilentAccountsPath <> '';
end;

function UseSilentDefaultPins: Boolean;
begin
  Result := SilentDefaultPinsPath <> '';
end;

function NeedUpgradeBackupPage: Boolean;
begin
  Result := (not IsFirstInstall) and FileExists(ExpandConstant('{commonappdata}\Checkpoint\logs\backup-secondary-missing.flag'));
end;

function NormalizeDigits(const S: String): String;
var I: Integer; C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if (C >= '0') and (C <= '9') then
      Result := Result + C
    else if C = #$0660 then Result := Result + '0'
    else if C = #$0661 then Result := Result + '1'
    else if C = #$0662 then Result := Result + '2'
    else if C = #$0663 then Result := Result + '3'
    else if C = #$0664 then Result := Result + '4'
    else if C = #$0665 then Result := Result + '5'
    else if C = #$0666 then Result := Result + '6'
    else if C = #$0667 then Result := Result + '7'
    else if C = #$0668 then Result := Result + '8'
    else if C = #$0669 then Result := Result + '9';
  end;
end;

function IsWeakPin(const Pin: String): Boolean;
var I: Integer; D: Char; P: String;
begin
  Result := False;
  P := NormalizeDigits(Pin);
  if (P = '1234') or (P = '1111') or (P = '0000') then
  begin
    Result := True;
    Exit;
  end;
  if Length(P) < 1 then Exit;
  D := P[1];
  Result := True;
  for I := 2 to Length(P) do
    if P[I] <> D then
    begin
      Result := False;
      Exit;
    end;
end;

function ValidateUsername(const U: String; var Err: String): Boolean;
var I: Integer; C: Char;
begin
  Result := False;
  if Trim(U) = '' then begin Err := 'نام کاربری را وارد کنید.'; Exit; end;
  if (Length(U) < 3) or (Length(U) > 20) then begin Err := 'نام کاربری باید ۳ تا ۲۰ نویسه باشد.'; Exit; end;
  for I := 1 to Length(U) do
  begin
    C := U[I];
    if not ( ((C >= 'A') and (C <= 'Z')) or ((C >= 'a') and (C <= 'z')) or ((C >= '0') and (C <= '9')) or (C = '_') or (C = '.') ) then
    begin
      Err := 'نام کاربری فقط حرف لاتین، رقم، _ و . می‌پذیرد.';
      Exit;
    end;
  end;
  Result := True;
end;

function ValidatePinPairEx(const Pin, Pin2: String; var Err: String; var ErrField: Integer): Boolean;
var P, P2: String;
begin
  Result := False;
  ErrField := 1;
  P := NormalizeDigits(Pin);
  P2 := NormalizeDigits(Pin2);
  if Trim(P) = '' then begin Err := 'رمز را وارد کنید.'; Exit; end;
  if (Length(P) < 4) or (Length(P) > 8) then begin Err := 'رمز باید ۴ تا ۸ رقم باشد.'; Exit; end;
  if IsWeakPin(P) then begin Err := 'این رمز ساده است و پذیرفته نمی‌شود.'; Exit; end;
  ErrField := 2;
  if Trim(P2) = '' then begin Err := 'رمز را دوباره وارد کنید.'; Exit; end;
  if P2 <> P then begin Err := 'دو رمز یکی نیستند.'; Exit; end;
  Result := True;
end;

procedure ClearQuery3Errors(E0, E1, E2: TNewStaticText);
begin
  if E0 <> nil then begin E0.Caption := ''; E0.Visible := False; end;
  if E1 <> nil then begin E1.Caption := ''; E1.Visible := False; end;
  if E2 <> nil then begin E2.Caption := ''; E2.Visible := False; end;
end;

procedure ShowQuery3Error(E0, E1, E2: TNewStaticText; FieldIdx: Integer; const Msg: String);
begin
  ClearQuery3Errors(E0, E1, E2);
  if FieldIdx = 0 then begin E0.Caption := Msg; E0.Visible := True; end
  else if FieldIdx = 1 then begin E1.Caption := Msg; E1.Visible := True; end
  else begin E2.Caption := Msg; E2.Visible := True; end;
end;

procedure AttachQuery3FieldErrors(Page: TInputQueryWizardPage; var E0, E1, E2: TNewStaticText);
var Gap: Integer;
begin
  Gap := ScaleY(12);
  Page.Edits[1].Top := Page.Edits[1].Top + Gap;
  Page.Edits[2].Top := Page.Edits[2].Top + 2 * Gap;
  Page.PromptLabels[1].Top := Page.Edits[1].Top - ScaleY(13);
  Page.PromptLabels[2].Top := Page.Edits[2].Top - ScaleY(13);

  E0 := TNewStaticText.Create(Page);
  E0.Parent := Page.Surface;
  E0.Font.Color := clRed;
  E0.Caption := '';
  E0.Visible := False;
  E0.Top := Page.Edits[0].Top + Page.Edits[0].Height + 2;
  E0.Left := Page.Edits[0].Left;
  E0.Width := Page.Edits[0].Width;

  E1 := TNewStaticText.Create(Page);
  E1.Parent := Page.Surface;
  E1.Font.Color := clRed;
  E1.Caption := '';
  E1.Visible := False;
  E1.Top := Page.Edits[1].Top + Page.Edits[1].Height + 2;
  E1.Left := Page.Edits[1].Left;
  E1.Width := Page.Edits[1].Width;

  E2 := TNewStaticText.Create(Page);
  E2.Parent := Page.Surface;
  E2.Font.Color := clRed;
  E2.Caption := '';
  E2.Visible := False;
  E2.Top := Page.Edits[2].Top + Page.Edits[2].Height + 2;
  E2.Left := Page.Edits[2].Left;
  E2.Width := Page.Edits[2].Width;
end;

procedure AttachPinPairFieldErrors(Page: TInputQueryWizardPage; var E0, E1: TNewStaticText);
var Gap: Integer;
begin
  Gap := ScaleY(12);
  Page.Edits[1].Top := Page.Edits[1].Top + Gap;
  Page.PromptLabels[1].Top := Page.Edits[1].Top - ScaleY(13);

  E0 := TNewStaticText.Create(Page);
  E0.Parent := Page.Surface;
  E0.Font.Color := clRed;
  E0.Caption := '';
  E0.Visible := False;
  E0.Top := Page.Edits[0].Top + Page.Edits[0].Height + 2;
  E0.Left := Page.Edits[0].Left;
  E0.Width := Page.Edits[0].Width;

  E1 := TNewStaticText.Create(Page);
  E1.Parent := Page.Surface;
  E1.Font.Color := clRed;
  E1.Caption := '';
  E1.Visible := False;
  E1.Top := Page.Edits[1].Top + Page.Edits[1].Height + 2;
  E1.Left := Page.Edits[1].Left;
  E1.Width := Page.Edits[1].Width;
end;

procedure ClearPinPairErrors(E0, E1: TNewStaticText);
begin
  if E0 <> nil then begin E0.Caption := ''; E0.Visible := False; end;
  if E1 <> nil then begin E1.Caption := ''; E1.Visible := False; end;
end;

procedure ShowPinPairError(E0, E1: TNewStaticText; FieldIdx: Integer; const Msg: String);
begin
  ClearPinPairErrors(E0, E1);
  if FieldIdx = 1 then begin E0.Caption := Msg; E0.Visible := True; end
  else begin E1.Caption := Msg; E1.Visible := True; end;
end;

function DriveLetterOfPath(const P: String): String;
var S: String;
begin
  S := Trim(P);
  if Length(S) >= 2 then
    Result := UpperCase(Copy(S, 1, 1))
  else
    Result := '';
end;

function SameDriveAsProgramData(const Dir: String): Boolean;
var A, B: String;
begin
  A := DriveLetterOfPath('C:\ProgramData');
  B := DriveLetterOfPath(Dir);
  Result := (B <> '') and (A = B);
end;

procedure SaveAccountsJson;
var
  S: AnsiString;
begin
  S := '{' + #13#10 +
    '  "manager1User": "' + Manager1User + '",' + #13#10 +
    '  "manager1Pin": "' + NormalizeDigits(Manager1Pin) + '",' + #13#10 +
    '  "manager2User": "' + Manager2User + '",' + #13#10 +
    '  "manager2Pin": "' + NormalizeDigits(Manager2Pin) + '",' + #13#10 +
    '  "operatorUser": "' + OperatorUser + '",' + #13#10 +
    '  "operatorPin": "' + NormalizeDigits(OperatorPin) + '",' + #13#10 +
    '  "secondaryBackupDir": "' + SecondaryBackupDir + '"' + #13#10 +
    '}';
  SaveStringToFile(AccountsJsonPath, S, False);
end;

procedure UpdateBackupDirWarnings;
var Dir: String;
begin
  if BackupDirPage = nil then Exit;
  Dir := Trim(BackupDirPage.Values[0]);
  if BackupEmptyWarnLabel <> nil then
    BackupEmptyWarnLabel.Visible := (Dir = '');
  if BackupSameDriveWarnLabel <> nil then
    BackupSameDriveWarnLabel.Visible := (Dir <> '') and SameDriveAsProgramData(Dir);
end;

procedure LoadDefaultPinUsers;
var
  ServerExe, OutFile: String;
  Lines: TArrayOfString;
  I, N: Integer;
  Rc: Integer;
begin
  DefaultPinUserCount := 0;
  SetArrayLength(DefaultPinUsernames, 0);
  if IsFirstInstall then Exit;
  ServerExe := ExpandConstant('{pf}\Checkpoint\Server\GameClub.Server.exe');
  if not FileExists(ServerExe) then Exit;
  OutFile := ExpandConstant('{tmp}\default-pins.txt');
  if Exec('cmd.exe', '/c ""' + ServerExe + '" --check-default-pins > "' + OutFile + '" 2>nul"', '', SW_HIDE, ewWaitUntilTerminated, Rc) then
  begin
    if LoadStringsFromFile(OutFile, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        if Trim(Lines[I]) <> '' then
        begin
          N := GetArrayLength(DefaultPinUsernames);
          if N >= MAX_DEFAULT_PIN_PAGES then
          begin
            // TODO(spec): installer supports MAX_DEFAULT_PIN_PAGES accounts per upgrade; raise if product needs more.
            Break;
          end;
          SetArrayLength(DefaultPinUsernames, N + 1);
          DefaultPinUsernames[N] := Trim(Lines[I]);
        end;
  end;
  DefaultPinUserCount := GetArrayLength(DefaultPinUsernames);
end;

procedure SaveDefaultPinsJson;
var
  S, U, P: String;
  I: Integer;
begin
  S := '[' + #13#10;
  for I := 0 to DefaultPinUserCount - 1 do
  begin
    U := DefaultPinUsernames[I];
    if I < GetArrayLength(DefaultPinStoredPins) then
      P := DefaultPinStoredPins[I]
    else
      P := '';
    if I > 0 then S := S + ',' + #13#10;
    S := S + '  { "username": "' + U + '", "pin": "' + P + '" }';
  end;
  S := S + #13#10 + ']';
  if UseSilentDefaultPins then
    DefaultPinsJsonPath := SilentDefaultPinsPath
  else
    SaveStringToFile(DefaultPinsJsonPath, S, False);
end;

function GetDefaultPinsFile(Param: String): String;
begin
  if DefaultPinsJsonPath = '' then
    Result := ExpandConstant('{param:DEFAULTPINS|}')
  else
    Result := DefaultPinsJsonPath;
end;

function UpgradeHasDefaultPinsFile: Boolean;
var F: String;
begin
  F := Trim(GetDefaultPinsFile(''));
  Result := (F <> '') and FileExists(F);
end;

function GetInstalledDisplayVersion: String;
var
  UninstallKey: String;
  UninstallKeyWow: String;
begin
  UninstallKey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}_is1';
  UninstallKeyWow := 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}_is1';
  if not RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM, UninstallKeyWow, 'DisplayVersion', Result) then
      Result := '';
end;

function CompareVersionParts(const A, B: String): Integer;
var
  SA, SB: String;
  I, J, NA, NB: Integer;
begin
  SA := A;
  SB := B;
  while (SA <> '') or (SB <> '') do
  begin
    I := Pos('.', SA);
    J := Pos('.', SB);
    if I = 0 then I := Length(SA) + 1;
    if J = 0 then J := Length(SB) + 1;
    NA := StrToIntDef(Copy(SA, 1, I - 1), 0);
    NB := StrToIntDef(Copy(SB, 1, J - 1), 0);
    if NA > NB then begin Result := 1; Exit; end;
    if NA < NB then begin Result := -1; Exit; end;
    Delete(SA, 1, I);
    Delete(SB, 1, J);
  end;
  Result := 0;
end;

function DefaultPinPageIndex(PageID: Integer): Integer;
var I: Integer;
begin
  Result := -1;
  for I := 0 to DefaultPinUserCount - 1 do
    if DefaultPinPageIds[I] = PageID then
    begin
      Result := I;
      Exit;
    end;
end;

function ShouldSkipDefaultPinPage(const Index: Integer): Boolean;
begin
  Result := True;
  if UseSilentDefaultPins then Exit;
  if Index >= DefaultPinUserCount then Exit;
  Result := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
var I: Integer;
begin
  Result := False;
  if not IsFirstInstall then
  begin
    if (Manager1Page <> nil) and ((PageID = Manager1Page.ID) or (PageID = Manager2Page.ID) or (PageID = OperatorPage.ID)) then
      Result := True;
    if (BackupDirPage <> nil) and (PageID = BackupDirPage.ID) then
      Result := not NeedUpgradeBackupPage;
    for I := 0 to DefaultPinUserCount - 1 do
      if PageID = DefaultPinPageIds[I] then
        Result := ShouldSkipDefaultPinPage(I);
    Exit;
  end;
  if UseSilentAccounts then
  begin
    if (PageID = Manager1Page.ID) or (PageID = Manager2Page.ID) or (PageID = OperatorPage.ID) or (PageID = BackupDirPage.ID) then
      Result := True;
  end;
end;

function QueryOpenSessionCount(var Count: Integer; var Unknown: Boolean): Boolean;
var
  ScriptPath, OutFile, Line: String;
  Lines: TArrayOfString;
  Rc: Integer;
begin
  Unknown := False;
  Count := 0;
  Result := False;
  ScriptPath := ExpandConstant('{pf}\Checkpoint\Server\tools\Get-InstallerOpenSessionCount.ps1');
  if not FileExists(ScriptPath) then
  begin
    Unknown := True;
    Exit;
  end;
  OutFile := ExpandConstant('{tmp}\open-sessions-count.txt');
  if Exec('cmd.exe', '/c powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" > "' + OutFile + '" 2>nul', '', SW_HIDE, ewWaitUntilTerminated, Rc) then
    if LoadStringsFromFile(OutFile, Lines) and (GetArrayLength(Lines) > 0) then
    begin
      Line := Trim(Lines[0]);
      if Pos('COUNT=', Line) = 1 then
      begin
        Count := StrToIntDef(Copy(Line, 7, Length(Line)), 0);
        Result := True;
        Exit;
      end;
      if Line = 'UNKNOWN' then
      begin
        Unknown := True;
        Exit;
      end;
    end;
  Unknown := True;
end;

function ConfirmUpgradeDespiteOpenSessions: Boolean;
var
  Count: Integer;
  Unknown: Boolean;
  Msg: String;
  Buttons: TArrayOfString;
  Choice: Integer;
begin
  Result := True;
  if IsFirstInstall or WizardSilent then Exit;
  if ExpandConstant('{param:SKIPSESSIONCHECK|}') <> '' then Exit;
  QueryOpenSessionCount(Count, Unknown);
  if (not Unknown) and (Count = 0) then Exit;

  SetArrayLength(Buttons, 2);
  Buttons[0] := 'ارتقا';
  Buttons[1] := 'انصراف';

  if Unknown then
    Msg := 'وضعیت جلسه‌های باز معلوم نیست. باز هم ارتقا انجام شود؟'
  else
    Msg := Format('%1 جلسهٔ باز در جریان است. با ارتقا، پنل اپراتور روی این رایانه و روی گوشی‌ها تا روشن شدن دوبارهٔ سرور باز نمی‌شود.', [IntToStr(Count)]);

  Choice := TaskDialogMsgBox('ارتقای چک‌پوینت', Msg, mbConfirmation, MB_YESNO, Buttons, 0);
  Result := (Choice = IDYES);
end;

function ValidateAccountPage(Page: TInputQueryWizardPage; E0, E1, E2: TNewStaticText;
  var U, P, P2: String; const OtherUsers: TArrayOfString): Boolean;
var Err: String; ErrField, I: Integer;
begin
  Result := True;
  ClearQuery3Errors(E0, E1, E2);
  U := Trim(Page.Values[0]);
  P := Page.Values[1];
  P2 := Page.Values[2];

  if not ValidateUsername(U, Err) then
  begin
    ShowQuery3Error(E0, E1, E2, 0, Err);
    WizardForm.ActiveControl := Page.Edits[0];
    Result := False;
    Exit;
  end;

  for I := 0 to GetArrayLength(OtherUsers) - 1 do
    if CompareText(U, OtherUsers[I]) = 0 then
    begin
      if I = 0 then
        ShowQuery3Error(E0, E1, E2, 0, 'این نام کاربری برای مدیر اول انتخاب شده است.')
      else
        ShowQuery3Error(E0, E1, E2, 0, 'این نام کاربری برای یکی از مدیرها انتخاب شده است.');
      WizardForm.ActiveControl := Page.Edits[0];
      Result := False;
      Exit;
    end;

  if not ValidatePinPairEx(P, P2, Err, ErrField) then
  begin
    ShowQuery3Error(E0, E1, E2, ErrField, Err);
    WizardForm.ActiveControl := Page.Edits[ErrField];
    Result := False;
    Exit;
  end;
end;

function ValidateDefaultPinPageAt(Index: Integer): Boolean;
var
  Page: TInputQueryWizardPage;
  Err: String;
  ErrField: Integer;
  Pin, Pin2: String;
begin
  Result := False;
  Page := DefaultPinPages[Index];
  Pin := Page.Values[0];
  Pin2 := Page.Values[1];
  ClearPinPairErrors(DefaultPinErrPin[Index], DefaultPinErrRepeat[Index]);
  if not ValidatePinPairEx(Pin, Pin2, Err, ErrField) then
  begin
    ShowPinPairError(DefaultPinErrPin[Index], DefaultPinErrRepeat[Index], ErrField, Err);
    WizardForm.ActiveControl := Page.Edits[ErrField - 1];
    Exit;
  end;
  if GetArrayLength(DefaultPinStoredPins) < DefaultPinUserCount then
    SetArrayLength(DefaultPinStoredPins, DefaultPinUserCount);
  DefaultPinStoredPins[Index] := NormalizeDigits(Pin);
  Result := True;
  if Index = DefaultPinUserCount - 1 then
    SaveDefaultPinsJson;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Others: TArrayOfString;
begin
  Result := True;

  if (CurPageID = wpReady) and (not IsFirstInstall) then
  begin
    Result := ConfirmUpgradeDespiteOpenSessions;
    Exit;
  end;

  if (Manager1Page <> nil) and (CurPageID = Manager1Page.ID) then
  begin
    SetArrayLength(Others, 0);
    if not ValidateAccountPage(Manager1Page, M1Err0, M1Err1, M1Err2, Manager1User, Manager1Pin, Manager1Pin2, Others) then
      Result := False;
    Exit;
  end;

  if (Manager2Page <> nil) and (CurPageID = Manager2Page.ID) then
  begin
    SetArrayLength(Others, 1);
    Others[0] := Manager1User;
    if not ValidateAccountPage(Manager2Page, M2Err0, M2Err1, M2Err2, Manager2User, Manager2Pin, Manager2Pin2, Others) then
      Result := False;
    Exit;
  end;

  if (OperatorPage <> nil) and (CurPageID = OperatorPage.ID) then
  begin
    SetArrayLength(Others, 2);
    Others[0] := Manager1User;
    Others[1] := Manager2User;
    if not ValidateAccountPage(OperatorPage, OpErr0, OpErr1, OpErr2, OperatorUser, OperatorPin, OperatorPin2, Others) then
      Result := False;
    Exit;
  end;

  if (BackupDirPage <> nil) and (CurPageID = BackupDirPage.ID) then
  begin
    SecondaryBackupDir := BackupDirPage.Values[0];
    SaveAccountsJson;
    Exit;
  end;

  if DefaultPinPageIndex(CurPageID) >= 0 then
    Result := ValidateDefaultPinPageAt(DefaultPinPageIndex(CurPageID));
end;

procedure CurPageChanged(CurPageID: Integer);
var BaseWelcome, Wv2Line: String; Idx: Integer;
begin
  if CurPageID = wpReady then Exit;

  if (Manager1Page <> nil) and (CurPageID = Manager1Page.ID) then ClearQuery3Errors(M1Err0, M1Err1, M1Err2);
  if (Manager2Page <> nil) and (CurPageID = Manager2Page.ID) then ClearQuery3Errors(M2Err0, M2Err1, M2Err2);
  if (OperatorPage <> nil) and (CurPageID = OperatorPage.ID) then ClearQuery3Errors(OpErr0, OpErr1, OpErr2);

  Idx := DefaultPinPageIndex(CurPageID);
  if Idx >= 0 then
    ClearPinPairErrors(DefaultPinErrPin[Idx], DefaultPinErrRepeat[Idx]);

  if CurPageID = wpWelcome then
  begin
    if IsFirstInstall then
    begin
      WizardForm.WelcomeLabel1.Caption := 'به نصب چک‌پوینت خوش آمدید';
      BaseWelcome := 'این برنامه سرور چک‌پوینت، پایگاه داده و پنجرهٔ پیشخوان را روی این رایانه نصب می‌کند. در چند مرحلهٔ بعد، حساب‌های مدیر و اپراتور را با رمز تازه می‌سازید.';
      if not IsWebView2Installed then
        Wv2Line := #13#10 + 'WebView2 روی این رایانه نیست و همراه همین نصاب نصب می‌شود؛ اینترنت لازم نیست.'
      else
        Wv2Line := '';
      WizardForm.WelcomeLabel2.Caption := BaseWelcome + Wv2Line;
    end
    else
    begin
      WizardForm.WelcomeLabel1.Caption := 'ارتقای چک‌پوینت';
      WizardForm.WelcomeLabel2.Caption :=
        'چک‌پوینت از نسخهٔ ' + GetInstalledDisplayVersion + ' به {#AppVersion} ارتقا پیدا می‌کند. داده‌ها، حساب‌ها و تنظیم‌ها همان می‌مانند و پیش از ارتقا یک پشتیبان جدا گرفته می‌شود. سرور چند لحظه خاموش می‌شود و در این مدت پنل و کیوسک‌ها به سرور وصل نیستند.';
    end;
  end;
  if (BackupDirPage <> nil) and (CurPageID = BackupDirPage.ID) then
    UpdateBackupDirWarnings;

  if CurPageID = wpFinished then
    UpdateServerFinishPage;
end;

procedure InitializeWizard;
var PrevId: Integer; I: Integer; U: String; P: TInputQueryWizardPage;
begin
  SilentAccountsPath := ExpandConstant('{param:ACCOUNTS|}');
  SilentDefaultPinsPath := ExpandConstant('{param:DEFAULTPINS|}');
  DefaultPinsJsonPath := ExpandConstant('{tmp}\default-pin-replacements.json');
  AccountsJsonPath := ExpandConstant('{tmp}\checkpoint-accounts.json');
  if SilentAccountsPath <> '' then
    AccountsJsonPath := SilentAccountsPath;

  if IsFirstInstall then
  begin
    Manager1Page := CreateInputQueryPage(wpWelcome,
      'حساب مدیر اول', 'هر مدیر حساب جدای خودش را دارد.', '');
    Manager1Page.Add('نام کاربری:', False);
    Manager1Page.Add('رمز:', True);
    Manager1Page.Add('تکرار رمز:', True);
    AttachQuery3FieldErrors(Manager1Page, M1Err0, M1Err1, M1Err2);

    Manager2Page := CreateInputQueryPage(Manager1Page.ID,
      'حساب مدیر دوم', 'هر مدیر حساب جدای خودش را دارد.', ' ');
    Manager2Page.Add('نام کاربری:', False);
    Manager2Page.Add('رمز:', True);
    Manager2Page.Add('تکرار رمز:', True);
    AttachQuery3FieldErrors(Manager2Page, M2Err0, M2Err1, M2Err2);

    OperatorPage := CreateInputQueryPage(Manager2Page.ID,
      'حساب اپراتور', 'اپراتور با این حساب وارد پنل می‌شود.', ' ');
    OperatorPage.Add('نام کاربری:', False);
    OperatorPage.Add('رمز:', True);
    OperatorPage.Add('تکرار رمز:', True);
    AttachQuery3FieldErrors(OperatorPage, OpErr0, OpErr1, OpErr2);

    BackupDirPage := CreateInputDirPage(OperatorPage.ID,
      'پوشهٔ دوم پشتیبان',
      'پشتیبان روزانه ساعت ۹ صبح در C:\ProgramData\Checkpoint\backups ساخته می‌شود. یک پوشه روی دیسک دیگر یا فلش انتخاب کنید تا یک نسخه هم آنجا ذخیره شود. هیچ پشتیبانی خودکار پاک نمی‌شود.',
      'پوشهٔ دوم', False, '');
    BackupEmptyWarnLabel := TNewStaticText.Create(BackupDirPage);
    BackupEmptyWarnLabel.Parent := BackupDirPage.Surface;
    BackupEmptyWarnLabel.Caption := 'بدون پوشهٔ دوم، فقط یک نسخه از پشتیبان روی همین دیسک می‌ماند. بعداً می‌توانید پوشه را اضافه کنید.';
    BackupEmptyWarnLabel.Font.Color := $0000A5FF;
    BackupEmptyWarnLabel.Visible := False;
    BackupSameDriveWarnLabel := TNewStaticText.Create(BackupDirPage);
    BackupSameDriveWarnLabel.Parent := BackupDirPage.Surface;
    BackupSameDriveWarnLabel.Caption := 'این پوشه روی همان دیسک پشتیبان اصلی است. اگر این دیسک خراب شود، هر دو از دست می‌روند.';
    BackupSameDriveWarnLabel.Font.Color := $0000A5FF;
    BackupSameDriveWarnLabel.Visible := False;
  end
  else
  begin
    LoadDefaultPinUsers;
    SetArrayLength(DefaultPinStoredPins, DefaultPinUserCount);
    PrevId := wpWelcome;
    for I := 0 to DefaultPinUserCount - 1 do
    begin
      U := DefaultPinUsernames[I];
      P := CreateInputQueryPage(PrevId,
        'عوض کردن رمز پیش‌فرض',
        Format('رمز حساب %s هنوز رمز پیش‌فرض است و دیگر پذیرفته نمی‌شود. یک رمز تازه بگذارید.', [U]), '');
      P.Add('رمز تازه:', True);
      P.Add('تکرار رمز تازه:', True);
      AttachPinPairFieldErrors(P, DefaultPinErrPin[I], DefaultPinErrRepeat[I]);
      DefaultPinPages[I] := P;
      DefaultPinPageIds[I] := P.ID;
      PrevId := P.ID;
    end;

    if NeedUpgradeBackupPage then
    begin
      if PrevId = wpWelcome then
        BackupDirPage := CreateInputDirPage(wpWelcome,
          'پوشهٔ دوم پشتیبان',
          'پشتیبان روزانه ساعت ۹ صبح در C:\ProgramData\Checkpoint\backups ساخته می‌شود. یک پوشه روی دیسک دیگر یا فلش انتخاب کنید تا یک نسخه هم آنجا ذخیره شود. هیچ پشتیبانی خودکار پاک نمی‌شود.',
          'پوشهٔ دوم', False, '')
      else
        BackupDirPage := CreateInputDirPage(PrevId,
          'پوشهٔ دوم پشتیبان',
          'پشتیبان روزانه ساعت ۹ صبح در C:\ProgramData\Checkpoint\backups ساخته می‌شود. یک پوشه روی دیسک دیگر یا فلش انتخاب کنید تا یک نسخه هم آنجا ذخیره شود. هیچ پشتیبانی خودکار پاک نمی‌شود.',
          'پوشهٔ دوم', False, '');
      BackupEmptyWarnLabel := TNewStaticText.Create(BackupDirPage);
      BackupEmptyWarnLabel.Parent := BackupDirPage.Surface;
      BackupEmptyWarnLabel.Caption := 'بدون پوشهٔ دوم، فقط یک نسخه از پشتیبان روی همین دیسک می‌ماند. بعداً می‌توانید پوشه را اضافه کنید.';
      BackupEmptyWarnLabel.Font.Color := $0000A5FF;
      BackupEmptyWarnLabel.Visible := False;
      BackupSameDriveWarnLabel := TNewStaticText.Create(BackupDirPage);
      BackupSameDriveWarnLabel.Parent := BackupDirPage.Surface;
      BackupSameDriveWarnLabel.Caption := 'این پوشه روی همان دیسک پشتیبان اصلی است. اگر این دیسک خراب شود، هر دو از دست می‌روند.';
      BackupSameDriveWarnLabel.Font.Color := $0000A5FF;
      BackupSameDriveWarnLabel.Visible := False;
    end;
  end;

  if UseSilentDefaultPins then
    DefaultPinsJsonPath := SilentDefaultPinsPath;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var Wv2Line, FolderLine: String;
begin
  if not FileExists(ExpandConstant('{reg:HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5},pv')) then
    Wv2Line := 'WebView2: همراه نصاب نصب می‌شود'
  else
    Wv2Line := 'WebView2: نصب است';
  if IsFirstInstall and not UseSilentAccounts then
  begin
    if Trim(SecondaryBackupDir) = '' then
      FolderLine := 'پوشهٔ دوم پشتیبان: انتخاب نشده'
    else
      FolderLine := 'پوشهٔ دوم پشتیبان: ' + SecondaryBackupDir;
    Result :=
      'محل برنامه: C:\Program Files\Checkpoint' + NewLine +
      'محل داده‌ها: C:\ProgramData\Checkpoint' + NewLine +
      'مدیر اول: ' + Manager1User + ' — مدیر دوم: ' + Manager2User + ' — اپراتور: ' + OperatorUser + NewLine +
      FolderLine + NewLine +
      Wv2Line;
  end
  else
    Result :=
      MemoDirInfo + NewLine + NewLine +
      MemoTypeInfo + NewLine + NewLine +
      MemoComponentsInfo + NewLine + NewLine +
      MemoGroupInfo + NewLine + NewLine +
      MemoTasksInfo;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if MaxProgress > 0 then
    WizardForm.StatusLabel.Caption := 'در حال کپی کردن فایل‌ها…';
end;

function UpgradeDefaultPinsReady: Boolean;
begin
  if IsFirstInstall then
    Result := True
  else if UseSilentDefaultPins then
    Result := True
  else if DefaultPinUserCount = 0 then
    Result := True
  else
    Result := FileExists(DefaultPinsJsonPath);
end;