var
  Manager1Page, Manager2Page, OperatorPage: TInputQueryWizardPage;
  BackupDirPage: TInputDirWizardPage;
  Manager1User, Manager1Pin, Manager1Pin2: String;
  Manager2User, Manager2Pin, Manager2Pin2: String;
  OperatorUser, OperatorPin, OperatorPin2: String;
  SecondaryBackupDir: String;
  AccountsJsonPath: String;
  SilentAccountsPath: String;
  InstallingPageActive: Boolean;

function IsFirstInstall: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonappdata}\Checkpoint\config\server.json'));
end;

function IsUpgradeInstallCheck: Boolean;
begin
  Result := not IsFirstInstall;
end;

function UseSilentAccounts: Boolean;
begin
  Result := SilentAccountsPath <> '';
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
var I: Integer; D: Char;
begin
  Result := False;
  if (Pin = '1234') or (Pin = '1111') or (Pin = '0000') then
  begin
    Result := True;
    Exit;
  end;
  if Length(Pin) < 1 then Exit;
  D := Pin[1];
  Result := True;
  for I := 2 to Length(Pin) do
    if Pin[I] <> D then
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

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if not IsFirstInstall then
  begin
    if (PageID = Manager1Page.ID) or (PageID = Manager2Page.ID) or (PageID = OperatorPage.ID) or (PageID = BackupDirPage.ID) then
      Result := True;
    Exit;
  end;
  if UseSilentAccounts then
  begin
    if (PageID = Manager1Page.ID) or (PageID = Manager2Page.ID) or (PageID = OperatorPage.ID) or (PageID = BackupDirPage.ID) then
      Result := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Err: String; U: String;
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
  else if CurPageID = BackupDirPage.ID then
  begin
    SecondaryBackupDir := BackupDirPage.Values[0];
    SaveAccountsJson;
    if Trim(SecondaryBackupDir) = '' then
    begin
      if MsgBox('بدون پوشهٔ دوم، فقط یک نسخه از پشتیبان روی همین دیسک می‌ماند. بعداً می‌توانید پوشه را اضافه کنید. ادامه می‌دهید؟', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;

procedure InitializeWizard;
begin
  SilentAccountsPath := ExpandConstant('{param:ACCOUNTS|}');
  AccountsJsonPath := ExpandConstant('{tmp}\checkpoint-accounts.json');
  if SilentAccountsPath <> '' then
    AccountsJsonPath := SilentAccountsPath;

  WizardForm.WelcomeLabel1.Caption := 'به نصب چک‌پوینت خوش آمدید';
  WizardForm.WelcomeLabel2.Caption := 'این برنامه سرور چک‌پوینت، پایگاه داده و پنجرهٔ پیشخوان را روی این رایانه نصب می‌کند. در چند مرحلهٔ بعد، حساب‌های مدیر و اپراتور را با رمز تازه می‌سازید.';

  if not IsFirstInstall then
    Exit;

  Manager1Page := CreateInputQueryPage(wpWelcome,
    'حساب مدیر اول', 'هر مدیر حساب جدای خودش را دارد.', '');
  Manager1Page.Add('نام کاربری:', False);
  Manager1Page.Add('رمز:', True);
  Manager1Page.Add('تکرار رمز:', True);

  Manager2Page := CreateInputQueryPage(Manager1Page.ID,
    'حساب مدیر دوم', 'هر مدیر حساب جدای خودش را دارد.',
    ' ');
  Manager2Page.Add('نام کاربری:', False);
  Manager2Page.Add('رمز:', True);
  Manager2Page.Add('تکرار رمز:', True);

  OperatorPage := CreateInputQueryPage(Manager2Page.ID,
    'حساب اپراتور', 'اپراتور با این حساب وارد پنل می‌شود.',
    ' ');
  OperatorPage.Add('نام کاربری:', False);
  OperatorPage.Add('رمز:', True);
  OperatorPage.Add('تکرار رمز:', True);

  BackupDirPage := CreateInputDirPage(OperatorPage.ID,
    'پوشهٔ دوم پشتیبان',
    'پشتیبان روزانه ساعت ۹ صبح در C:\ProgramData\Checkpoint\backups ساخته می‌شود. یک پوشه روی دیسک دیگر یا فلش انتخاب کنید تا یک نسخه هم آنجا ذخیره شود. هیچ پشتیبانی خودکار پاک نمی‌شود.',
    'پوشهٔ دوم', False, '');
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoUserInfoRecommended, MemoProgramInfo: String): String;
var Wv2Line: String;
begin
  if not FileExists(ExpandConstant('{reg:HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5},pv')) then
    Wv2Line := 'WebView2: همراه نصاب نصب می‌شود'
  else
    Wv2Line := 'WebView2: نصب است';
  if IsFirstInstall and not UseSilentAccounts then
  begin
    Result :=
      'محل برنامه: C:\Program Files\Checkpoint' + NewLine +
      'محل داده‌ها: C:\ProgramData\Checkpoint' + NewLine +
      'مدیر اول: ' + Manager1User + ' — مدیر دوم: ' + Manager2User + ' — اپراتور: ' + OperatorUser + NewLine +
      'پوشهٔ دوم پشتیبان: ' + SecondaryBackupDir + NewLine +
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