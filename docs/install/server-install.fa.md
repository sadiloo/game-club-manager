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
2. **برگشت به نسخهٔ قبل**: حذف برنامه، نصب نصاب نسخهٔ قبلی (پوشهٔ دادهٔ PostgreSQL باقی می‌ماند و دوباره به کار می‌رود — `CHECKPOINT_REINSTALL_KEPT_PGDATA` — و پیش از آن یک `pre-reinstall-kept-*.dump` گرفته می‌شود) و سپس بازیابی آخرین پشتیبان پیش از ارتقا (`C:\ProgramData\Checkpoint\backups\pre-upgrade-<نسخهٔ قبل>-<زمان>.dump` یا تازه‌ترین `checkpoint-*.dump`) با `powershell -ExecutionPolicy Bypass -File "C:\Program Files\Checkpoint\Server\tools\restore.ps1" -DumpPath "<فایل پشتیبان>"`.

Setup و پشتیبان‌گیری روزانه هیچ پشتیبانی را پاک نمی‌کنند؛ اگر `pg_dump` شکست بخورد فقط فایل ناقص یا خالیِ همان اجرا پاک و در گزارش ثبت می‌شود (`CHECKPOINT_PGDUMP_FAILED ... action=removed_own_failed_output`) و پشتیبان تازه هرگز روی فایل موجود نوشته نمی‌شود.

### گزینهٔ `/SKIPSESSIONCHECK=1` (فقط برای بازیابی)

عمداً در نسخهٔ انتشار مانده است. بررسی جلسه‌های باز در ارتقا را رد می‌کند (در حالت بی‌صدا کد ۱۳۱ و در حالت عادی پرسش جلسهٔ باز). فقط برای بازیابی به کار ببرید، وقتی سرور خاموش است و تعداد جلسه‌های باز خوانده نمی‌شود یا خودتان جلسه‌ها را بسته‌اید؛ در آن اجرا از جلسه‌های باز محافظت نمی‌شود. هر بار استفاده در گزارش ثبت می‌شود: `CHECKPOINT_SKIPSESSIONCHECK used=1 ...`. فقط مقدار `1` بررسی را رد می‌کند؛ `/SKIPSESSIONCHECK=0` یا هر مقدار دیگر نادیده گرفته و با `used=0` ثبت می‌شود. اگر ارتقای بی‌صدا به‌دلیل در دسترس نبودن سرور با کد ۱۳۱ متوقف شود، همان خط `CHECKPOINT_OPEN_SESSIONS_SILENT` فرمان `recovery_command=` را هم نشان می‌دهد.

کیوسک: کد **۱۵۰** برای شکست پس‌نصب — [kiosk-install.fa.md](kiosk-install.fa.md).

جزئیات مرحلهٔ شکست در `C:\ProgramData\Checkpoint\logs\checkpoint-setup-failure.ini`، `postinstall-phase-trace.log` و گزارش Inno (`/LOG=`) ثبت می‌شود. نصاب انتشار بدون کد هوک داخلی است؛ نصاب Hooks جداست و `/TESTFAILPHASE=` فقط در آن build فعال است.

جدول کامل انگلیسی: [installer/README.md](../installer/README.md).