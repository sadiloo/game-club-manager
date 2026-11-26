# نصب سرور چک‌پوینت

نصاب امضا ندارد؛ در صورت نمایش **Windows protected your PC** گزینهٔ **More info → Run anyway** را بزنید.

مسیرها ثابت‌اند:

- برنامه: `C:\Program Files\Checkpoint\Server\`
- داده: `C:\ProgramData\Checkpoint\`

پس از نصب، آدرس سرور را از صفحهٔ پایان نصاب یادداشت کنید (مثلاً `http://192.168.1.10:5000`). قبل از راه‌اندازی کیوسک‌ها، رمزهای کیوسک را از پیشخوان بسازید.

## کدهای خروج Setup.exe (سرور)

| کد | معنی |
|---|---|
| 0 | نصب/ارتقا موفق |
| 128 | نصب نسخهٔ قدیمی‌تر روی نسخهٔ جدیدتر (downgrade) |
| 129 | شکست پس‌نصب با کد نامشخص (fallback؛ هر مرحلهٔ مشخص کد جدا دارد) |
| 130 | دادهٔ PostgreSQL موجود نگه داشته شد؛ نصب تازه متوقف شد |
| 131 | ارتقای `/VERYSILENT` هنگام جلسهٔ باز، متوقف، یا **نامشخص** (سرور در دسترس نیست) — بدون پرسش؛ در گزارش: `CHECKPOINT_OPEN_SESSIONS_SILENT reason=open_session_count_unknown_server_unreachable` |
| 132 | فایل `/ACCOUNTS` نامعتبر (نام کاربری خالی، نقش نامعتبر، یا کمتر از سه حساب) |
| 133 | پس‌نصب: WebView2 |
| 134 | پس‌نصب: نگهداری pgdata در نصب مجدد |
| 135 | پس‌نصب: پایگاه داده |
| 136 | پس‌نصب: حساب‌ها |
| 137 | پس‌نصب: سرویس‌ها |
| 138 | پس‌نصب: فایروال |
| 139 | پس‌نصب: پشتیبان روزانه |
| 140 | پس‌نصب: روشن کردن سرور (start) |
| 141 | ارتقا: رمز پیش‌فرض (upgrade-pins) |
| 142 | ارتقا: توقف سرویس‌ها (upgrade-stop) |
| 143 | ارتقا: پشتیبان پیش از ارتقا (upgrade-backup) |
| 144 | ارتقا: استارت پس از ارتقا (upgrade-start) |

با اولین شکست در پس‌نصب، مراحل بعدی اجرا نمی‌شوند. در نصب تازهٔ ناموفق، سرویس‌ها در صورت نیاز متوقف می‌مانند (`CHECKPOINT_POSTINSTALL_ABORT_SERVICES_STOPPED` در گزارش). در ارتقای ناموفق، تلاش می‌شود داده و پشتیبان قبلی دست‌نخورده بماند و سرویس‌ها تا حد ممکن به وضعیت قبل برگردند. اگر پشتیبان پیش از ارتقای همین اجرا ساخته شده باشد، سرور دوباره روشن می‌شود (`CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE`). اگر ارتقا پیش از ساخته شدن این پشتیبان شکست بخورد (upgrade-pins، upgrade-stop یا upgrade-backup)، سرور چک‌پوینت **خاموش می‌ماند** تا پایگاه داده بدون پشتیبان به‌روز نشود (`CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup`). در این حالت علت را در گزارش نصب برطرف کنید و همان نصاب را دوباره اجرا کنید؛ سرور را دستی روشن نکنید.

در نصب تازهٔ ناموفق، خود Setup هم هر دو سرویس CheckpointServer و CheckpointPostgres را متوقف می‌کند (`CHECKPOINT_SETUP_FRESH_ABORT_SERVICES_STOPPED`).

سرور نصب‌شده هرگز رمزهای پیش‌فرض ۱۲۳۴/۱۱۱۱ نمی‌سازد: فهرست کاربران اولیه باید دست‌کم یک مدیر (Manager) داشته باشد، نقش فقط با نام پذیرفته می‌شود (`Owner`، `Manager`، `Operator`؛ عددهایی مثل `0` رد می‌شوند) و فهرست خالی یعنی شکست نصب.

## بازیابی پس از ارتقای ناموفق (MVP)

در این نسخه **برگشت خودکار به فایل‌های نسخهٔ قبل وجود ندارد** (پس از MVP). اگر در گزارش `CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED` آمد، Setup فرمان دقیق بازیابی را هم در گزارش می‌نویسد:

```
CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED recovery_command="D:\CheckpointServerSetup-1.2.3.exe" /SKIPSESSIONCHECK=1 /LOG="C:\ProgramData\Checkpoint\logs\setup-recovery.log"
```

یکی از این دو راه:

1. **اجرای دوبارهٔ نصاب جدید** پس از رفع علت: همان فرمان `recovery_command=` را با دسترسی Administrator اجرا کنید (`"<نصاب جدید>" /SKIPSESSIONCHECK=1 /LOG="C:\ProgramData\Checkpoint\logs\setup-recovery.log"`). این اجرا پیش از به‌روزرسانی پایگاه داده، پشتیبان خودش را می‌گیرد.
2. **برگشت به نسخهٔ قبل**. ترتیب مهم است: سرور نسخهٔ قدیمی هرگز نباید روی طرح (schema) جدیدتر پایگاه داده روشن شود؛ پس اول، در حالی که هیچ CheckpointServer روشن نیست، پشتیبان پیش از ارتقا بازیابی می‌شود و فقط بعد از آن نسخهٔ قدیمی نصب و روشن می‌شود. با دسترسی Administrator:
   1. سرور را متوقف کنید و جلوی روشن شدن دوباره‌اش را بگیرید: `sc.exe config CheckpointServer start= disabled` و سپس `sc.exe stop CheckpointServer` (سرویس CheckpointPostgres روشن می‌ماند).
   2. آخرین پشتیبان پیش از ارتقا (`C:\ProgramData\Checkpoint\backups\pre-upgrade-<نسخهٔ قبل>-<زمان>.dump` یا تازه‌ترین `checkpoint-*.dump` پیش از ارتقا) را با ابزار نصب‌شده بازیابی کنید: `powershell -ExecutionPolicy Bypass -File "C:\Program Files\Checkpoint\Server\tools\restore.ps1" -DumpPath "<فایل پشتیبان>"`. restore.ps1 اول پشتیبان `pre-restore-*.dump` خودش را می‌نویسد و خوانا بودن آن و فایل پشتیبان شما را بررسی می‌کند (`pg_restore --list`)؛ اگر هر کدام شکست بخورد متوقف می‌شود و به پایگاه داده دست نمی‌زند. فقط پس از آن طرح (schema) پایگاه داده را حذف و دوباره ساخته (`CHECKPOINT_RESTORE_SCHEMA_RECREATE ...` در `C:\ProgramData\Checkpoint\logs\restore.log`) و پشتیبان شما را در آن بازیابی می‌کند؛ پس هیچ جدول یا سابقهٔ مهاجرتی از نسخهٔ جدیدتر باقی نمی‌ماند و ارتقای بعدیِ نسخهٔ بازیابی‌شده بدون خطای `42P07 relation already exists` انجام می‌شود. اگر خود بازیابی شکست بخورد، خط `CHECKPOINT_RESTORE_FAILED ... recovery=restore.ps1 -DumpPath <پشتیبان pre-restore>` پشتیبان امنیتی را برای بازیابی نام می‌برد. تلاش آن برای روشن کردن CheckpointServer در پایان، تا وقتی سرویس غیرفعال است، اثری ندارد.
   3. فقط حالا برنامه را حذف کنید (پوشهٔ داده باقی می‌ماند) و نصاب نسخهٔ قبلی `CheckpointServerSetup` را نصب کنید. نصاب دادهٔ PostgreSQL باقی‌مانده را که اکنون روی طرح قدیمی است دوباره به کار می‌برد (`CHECKPOINT_REINSTALL_KEPT_PGDATA`، پس از گرفتن `pre-reinstall-kept-*.dump`)، سرویس CheckpointServer را دوباره ثبت و نسخهٔ قدیمی را روشن می‌کند.
   4. پیش از باز کردن دوبارهٔ مجموعه، نسخه و داده‌ها را بررسی کنید. پس از گام ۳، restore.ps1 را بدون متوقف کردن سرور دوباره اجرا نکنید.
   گام بازیابی (بازیابی پشتیبان نسخهٔ قدیمی روی طرح جدیدتر و سپس ارتقا تا آخرین نسخه بدون 42P07) با آزمون‌های سرور `RestoreOldDumpOntoNewerSchemaTests` پوشش داده می‌شود؛ کل این راه هنوز آزمون پذیرش خودکار ندارد.

Setup و پشتیبان‌گیری روزانه هیچ پشتیبانی را پاک نمی‌کنند؛ اگر `pg_dump` شکست بخورد فقط فایل ناقص یا خالیِ همان اجرا پاک و در گزارش ثبت می‌شود (`CHECKPOINT_PGDUMP_FAILED ... action=removed_own_failed_output`) و پشتیبان تازه هرگز روی فایل موجود نوشته نمی‌شود.

### گزینهٔ `/SKIPSESSIONCHECK=1` (فقط برای بازیابی)

عمداً در نسخهٔ انتشار مانده است. بررسی جلسه‌های باز در ارتقا را رد می‌کند (در حالت بی‌صدا کد ۱۳۱ و در حالت عادی پرسش جلسهٔ باز). فقط برای بازیابی به کار ببرید، وقتی سرور خاموش است و تعداد جلسه‌های باز خوانده نمی‌شود یا خودتان جلسه‌ها را بسته‌اید؛ در آن اجرا از جلسه‌های باز محافظت نمی‌شود.

- **فقط مقدار `1` بررسی را رد می‌کند** و هر بار استفاده در گزارش ثبت می‌شود: `CHECKPOINT_SKIPSESSIONCHECK used=1 value=1 context=silent-upgrade|interactive-upgrade note=operator recovery flag: ...`.
- **هر مقدار دیگر نادیده گرفته می‌شود و «نادیده گرفته شد» در گزارش ثبت می‌شود**: `/SKIPSESSIONCHECK=0`، `=yes`، `=true` و مانند آن‌ها خط `CHECKPOINT_SKIPSESSIONCHECK used=0 value=<مقدار> ... note=ignored: only /SKIPSESSIONCHECK=1 skips the open-session check` را می‌نویسند و بررسی جلسه‌های باز مثل نبودن گزینه انجام می‌شود.
- **ارتقای بی‌صدا با تعداد نامشخص جلسه‌ها با کد ۱۳۱ (131) متوقف می‌شود** (رفتار عمدی): بدون `/SKIPSESSIONCHECK=1`، ارتقای `/VERYSILENT` که نمی‌تواند تعداد جلسه‌ها را از سرور بپرسد (سرور در دسترس نیست) با کد 131 خارج می‌شود و خط `CHECKPOINT_OPEN_SESSIONS_SILENT reason=open_session_count_unknown_server_unreachable` را همراه با `recovery_command=` ثبت می‌کند.

کیوسک: کد **۱۵۰** برای شکست پس‌نصب — [kiosk-install.fa.md](kiosk-install.fa.md).

جزئیات مرحلهٔ شکست در `C:\ProgramData\Checkpoint\logs\checkpoint-setup-failure.ini`، `postinstall-phase-trace.log` و گزارش Inno (`/LOG=`) ثبت می‌شود. نصاب انتشار بدون کد هوک داخلی است؛ نصاب Hooks جداست و `/TESTFAILPHASE=` فقط در آن build فعال است.

جدول کامل انگلیسی: [installer/README.md](../installer/README.md).