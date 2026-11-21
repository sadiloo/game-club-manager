var
  Manager1Page, Manager2Page, OperatorPage: TInputQueryWizardPage;
  BackupDirPage: TInputDirWizardPage;
  DefaultPinPage1, DefaultPinPage2, DefaultPinPage3, DefaultPinPage4, DefaultPinPage5: TInputQueryWizardPage;
  BackupEmptyWarnLabel, BackupSameDriveWarnLabel: TNewStaticText;
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
  DefaultPin1, DefaultPin1b, DefaultPin2, DefaultPin2b, DefaultPin3, DefaultPin3b: String;
  DefaultPin4, DefaultPin4b, DefaultPin5, DefaultPin5b: String;

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

function ValidatePinPair(const Pin, Pin2: String; var Err: String): Boolean;
var P: String;
begin
  Result := False;
  P := NormalizeDigits(Pin);
  if Trim(P) = '' then begin Err := 'رمز را وارد کنید.'; Exit; end;
  if (Length(P) < 4) or (Length(P) > 8) then begin Err := 'رمز باید ۴ تا ۸ رقم باشد.'; Exit; end;
  if IsWeakPin(P) then begin Err := 'این رمز ساده است و پذیرفته نمی‌شود.'; Exit; end;
  if NormalizeDigits(Pin2) <> P then begin Err := 'دو رمز یکی نیستند.'; Exit; end;
  Result := True;
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
          SetArrayLength(DefaultPinUsernames, N + 1);
          DefaultPinUsernames[N] := Trim(Lines[I]);
        end;
  end;
  DefaultPinUserCount := GetArrayLength(DefaultPinUsernames);
  if DefaultPinUserCount > 5 then
    DefaultPinUserCount := 5;
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
    if I = 0 then P := NormalizeDigits(DefaultPin1)
    else if I = 1 then P := NormalizeDigits(DefaultPin2)
    else if I = 2 then P := NormalizeDigits(DefaultPin3)
    else if I = 3 then P := NormalizeDigits(DefaultPin4)
    else P := NormalizeDigits(DefaultPin5);
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

function GetInstalledDisplayVersion: String;
const
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}_is1';
  UninstallKeyWow = 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}_is1';
begin
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

function ShouldSkipDefaultPinPage(const Index: Integer): Boolean;
begin
  Result := True;
  if UseSilentDefaultPins then Exit;
  if Index >= DefaultPinUserCount then Exit;
  Result := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if not IsFirstInstall then
  begin
    if (Manager1Page <> nil) and ((PageID = Manager1Page.ID) or (PageID = Manager2Page.ID) or (PageID = OperatorPage.ID)) then
      Result := True;
    if (BackupDirPage <> nil) and (PageID = BackupDirPage.ID) then
      Result := not NeedUpgradeBackupPage;
    if (DefaultPinPage1 <> nil) and (PageID = DefaultPinPage1.ID) then Result := ShouldSkipDefaultPinPage(0);
    if (DefaultPinPage2 <> nil) and (PageID = DefaultPinPage2.ID) then Result := ShouldSkipDefaultPinPage(1);
    if (DefaultPinPage3 <> nil) and (PageID = DefaultPinPage3.ID) then Result := ShouldSkipDefaultPinPage(2);
    if (DefaultPinPage4 <> nil) and (PageID = DefaultPinPage4.ID) then Result := ShouldSkipDefaultPinPage(3);
    if (DefaultPinPage5 <> nil) and (PageID = DefaultPinPage5.ID) then Result := ShouldSkipDefaultPinPage(4);
    Exit;
  end;
  if UseSilentAccounts then
  begin
    if (PageID = Manager1Page.ID) or (PageID = Manager2Page.ID) or (PageID = OperatorPage.ID) or (PageID = BackupDirPage.ID) then
      Result := True;
  end;
end;

function ValidateDefaultPinPage(const Pin, Pin2: String): Boolean;
var Err: String;
begin
  Result := ValidatePinPair(Pin, Pin2, Err);
  if not Result then
    MsgBox(Err, mbError, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Err: String;
begin
  Result := True;
  Err := '';
  if CurPageID = Manager1Page.ID then
  begin
    Manager1User := Trim(Manager1Page.Values[0]);
    Manager1Pin := Manager1Page.Values[1];
    Manager1Pin2 := Manager1Page.Values[2];
    if not ValidateUsername(Manager1User, Err) then begin MsgBox(Err, mbError, MB_OK); Result := False; Exit; end;
    if not ValidatePinPair(Manager1Pin, Manager1Pin2, Err) then begin MsgBox(Err, mbError, MB_OK); Result := False; Exit; end;
  end
  else if CurPageID = Manager2Page.ID then
  begin
    Manager2User := Trim(Manager2Page.Values[0]);
    Manager2Pin := Manager2Page.Values[1];
    Manager2Pin2 := Manager2Page.Values[2];
    if not ValidateUsername(Manager2User, Err) then begin MsgBox(Err, mbError, MB_OK); Result := False; Exit; end;
    if CompareText(Manager2User, Manager1User) = 0 then begin MsgBox('این نام کاربری برای مدیر اول انتخاب شده است.', mbError, MB_OK); Result := False; Exit; end;
    if not ValidatePinPair(Manager2Pin, Manager2Pin2, Err) then begin MsgBox(Err, mbError, MB_OK); Result := False; Exit; end;
  end
  else if CurPageID = OperatorPage.ID then
  begin
    OperatorUser := Trim(OperatorPage.Values[0]);
    OperatorPin := OperatorPage.Values[1];
    OperatorPin2 := OperatorPage.Values[2];
    if not ValidateUsername(OperatorUser, Err) then begin MsgBox(Err, mbError, MB_OK); Result := False; Exit; end;
    if (CompareText(OperatorUser, Manager1User) = 0) or (CompareText(OperatorUser, Manager2User) = 0) then
    begin
      MsgBox('این نام کاربری برای یکی از مدیرها انتخاب شده است.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if not ValidatePinPair(OperatorPin, OperatorPin2, Err) then begin MsgBox(Err, mbError, MB_OK); Result := False; Exit; end;
  end
  else if (BackupDirPage <> nil) and (CurPageID = BackupDirPage.ID) then
  begin
    SecondaryBackupDir := BackupDirPage.Values[0];
    SaveAccountsJson;
    if Trim(SecondaryBackupDir) = '' then
    begin
      if MsgBox('بدون پوشهٔ دوم، فقط یک نسخه از پشتیبان روی همین دیسک می‌ماند. بعداً می‌توانید پوشه را اضافه کنید. ادامه می‌دهید؟', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end
  else if (DefaultPinPage1 <> nil) and (CurPageID = DefaultPinPage1.ID) then
  begin
    DefaultPin1 := DefaultPinPage1.Values[0]; DefaultPin1b := DefaultPinPage1.Values[1];
    Result := ValidateDefaultPinPage(DefaultPin1, DefaultPin1b);
  end
  else if (DefaultPinPage2 <> nil) and (CurPageID = DefaultPinPage2.ID) then
  begin
    DefaultPin2 := DefaultPinPage2.Values[0]; DefaultPin2b := DefaultPinPage2.Values[1];
    Result := ValidateDefaultPinPage(DefaultPin2, DefaultPin2b);
  end
  else if (DefaultPinPage3 <> nil) and (CurPageID = DefaultPinPage3.ID) then
  begin
    DefaultPin3 := DefaultPinPage3.Values[0]; DefaultPin3b := DefaultPinPage3.Values[1];
    Result := ValidateDefaultPinPage(DefaultPin3, DefaultPin3b);
  end
  else if (DefaultPinPage4 <> nil) and (CurPageID = DefaultPinPage4.ID) then
  begin
    DefaultPin4 := DefaultPinPage4.Values[0]; DefaultPin4b := DefaultPinPage4.Values[1];
    Result := ValidateDefaultPinPage(DefaultPin4, DefaultPin4b);
  end
  else if (DefaultPinPage5 <> nil) and (CurPageID = DefaultPinPage5.ID) then
  begin
    DefaultPin5 := DefaultPinPage5.Values[0]; DefaultPin5b := DefaultPinPage5.Values[1];
    Result := ValidateDefaultPinPage(DefaultPin5, DefaultPin5b);
  end;

  if Result and (DefaultPinUserCount > 0) and not UseSilentDefaultPins then
  begin
    if (DefaultPinPage5 <> nil) and (CurPageID = DefaultPinPage5.ID) then SaveDefaultPinsJson
    else if (DefaultPinUserCount = 4) and (DefaultPinPage4 <> nil) and (CurPageID = DefaultPinPage4.ID) then SaveDefaultPinsJson
    else if (DefaultPinUserCount = 3) and (DefaultPinPage3 <> nil) and (CurPageID = DefaultPinPage3.ID) then SaveDefaultPinsJson
    else if (DefaultPinUserCount = 2) and (DefaultPinPage2 <> nil) and (CurPageID = DefaultPinPage2.ID) then SaveDefaultPinsJson
    else if (DefaultPinUserCount = 1) and (DefaultPinPage1 <> nil) and (CurPageID = DefaultPinPage1.ID) then SaveDefaultPinsJson;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var BaseWelcome, Wv2Line: String;
begin
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
end;

procedure InitializeWizard;
var PrevId: Integer; I: Integer; U: String;
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

    Manager2Page := CreateInputQueryPage(Manager1Page.ID,
      'حساب مدیر دوم', 'هر مدیر حساب جدای خودش را دارد.', ' ');
    Manager2Page.Add('نام کاربری:', False);
    Manager2Page.Add('رمز:', True);
    Manager2Page.Add('تکرار رمز:', True);

    OperatorPage := CreateInputQueryPage(Manager2Page.ID,
      'حساب اپراتور', 'اپراتور با این حساب وارد پنل می‌شود.', ' ');
    OperatorPage.Add('نام کاربری:', False);
    OperatorPage.Add('رمز:', True);
    OperatorPage.Add('تکرار رمز:', True);

    BackupDirPage := CreateInputDirPage(OperatorPage.ID,
      'پوشهٔ دوم پشتیبان',
      'پشتیبان روزانه ساعت ۹ صبح در C:\ProgramData\Checkpoint\backups ساخته می‌شود. یک پوشه روی دیسک دیگر یا فلش انتخاب کنید تا یک نسخه هم آنجا ذخیره شود. هیچ پشتیبانی خودکار پاک نمی‌شود.',
      'پوشهٔ دوم', False, '');
    BackupEmptyWarnLabel := TNewStaticText.Create(BackupDirPage);
    BackupEmptyWarnLabel.Parent := BackupDirPage.Surface;
    BackupEmptyWarnLabel.Caption := 'بدون پوشهٔ دوم، فقط یک نسخه از پشتیبان روی همین دیسک می‌ماند. بعداً می‌توانید پوشه را اضافه کنید.';
    BackupEmptyWarnLabel.Font.Color := clOrange;
    BackupEmptyWarnLabel.Visible := False;
    BackupSameDriveWarnLabel := TNewStaticText.Create(BackupDirPage);
    BackupSameDriveWarnLabel.Parent := BackupDirPage.Surface;
    BackupSameDriveWarnLabel.Caption := 'این پوشه روی همان دیسک پشتیبان اصلی است. اگر این دیسک خراب شود، هر دو از دست می‌روند.';
    BackupSameDriveWarnLabel.Font.Color := clOrange;
    BackupSameDriveWarnLabel.Visible := False;
  end
  else
  begin
    LoadDefaultPinUsers;
    PrevId := wpWelcome;
    for I := 0 to DefaultPinUserCount - 1 do
    begin
      if I >= 5 then Break;
      U := DefaultPinUsernames[I];
      if I = 0 then
      begin
        DefaultPinPage1 := CreateInputQueryPage(PrevId,
          'عوض کردن رمز پیش‌فرض',
          Format('رمز حساب %s هنوز رمز پیش‌فرض است و دیگر پذیرفته نمی‌شود. یک رمز تازه بگذارید.', [U]), '');
        DefaultPinPage1.Add('رمز تازه:', True);
        DefaultPinPage1.Add('تکرار رمز تازه:', True);
        PrevId := DefaultPinPage1.ID;
      end
      else if I = 1 then
      begin
        DefaultPinPage2 := CreateInputQueryPage(PrevId,
          'عوض کردن رمز پیش‌فرض',
          Format('رمز حساب %s هنوز رمز پیش‌فرض است و دیگر پذیرفته نمی‌شود. یک رمز تازه بگذارید.', [U]), '');
        DefaultPinPage2.Add('رمز تازه:', True);
        DefaultPinPage2.Add('تکرار رمز تازه:', True);
        PrevId := DefaultPinPage2.ID;
      end
      else if I = 2 then
      begin
        DefaultPinPage3 := CreateInputQueryPage(PrevId,
          'عوض کردن رمز پیش‌فرض',
          Format('رمز حساب %s هنوز رمز پیش‌فرض است و دیگر پذیرفته نمی‌شود. یک رمز تازه بگذارید.', [U]), '');
        DefaultPinPage3.Add('رمز تازه:', True);
        DefaultPinPage3.Add('تکرار رمز تازه:', True);
        PrevId := DefaultPinPage3.ID;
      end
      else if I = 3 then
      begin
        DefaultPinPage4 := CreateInputQueryPage(PrevId,
          'عوض کردن رمز پیش‌فرض',
          Format('رمز حساب %s هنوز رمز پیش‌فرض است و دیگر پذیرفته نمی‌شود. یک رمز تازه بگذارید.', [U]), '');
        DefaultPinPage4.Add('رمز تازه:', True);
        DefaultPinPage4.Add('تکرار رمز تازه:', True);
        PrevId := DefaultPinPage4.ID;
      end
      else
      begin
        DefaultPinPage5 := CreateInputQueryPage(PrevId,
          'عوض کردن رمز پیش‌فرض',
          Format('رمز حساب %s هنوز رمز پیش‌فرض است و دیگر پذیرفته نمی‌شود. یک رمز تازه بگذارید.', [U]), '');
        DefaultPinPage5.Add('رمز تازه:', True);
        DefaultPinPage5.Add('تکرار رمز تازه:', True);
        PrevId := DefaultPinPage5.ID;
      end;
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
      BackupEmptyWarnLabel.Font.Color := clOrange;
      BackupEmptyWarnLabel.Visible := False;
      BackupSameDriveWarnLabel := TNewStaticText.Create(BackupDirPage);
      BackupSameDriveWarnLabel.Parent := BackupDirPage.Surface;
      BackupSameDriveWarnLabel.Caption := 'این پوشه روی همان دیسک پشتیبان اصلی است. اگر این دیسک خراب شود، هر دو از دست می‌روند.';
      BackupSameDriveWarnLabel.Font.Color := clOrange;
      BackupSameDriveWarnLabel.Visible := False;
    end;
  end;

  if UseSilentDefaultPins then
    DefaultPinsJsonPath := SilentDefaultPinsPath;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoUserInfoRecommended, MemoProgramInfo: String): String;
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
    Result := MemoProgramInfo;
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