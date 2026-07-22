# 1.2.1

- Added `/fine200` for administrators to register a fixed 200,000-toman penalty by replying to a member message.
- Added aliases `/penalty200`, `/جریمه200`, `جریمه ۲۰۰`, and `جریمه ۲۰۰۰۰۰`.
- The command auto-registers the replied member when needed, adds the penalty to the debt ledger, expires stale open invoices, and reports the new total debt.
- Updated administrator help, README, startup checklist, and the complete Persian test guide.
- Updated the Docker image tag to `latefeebox-stable:1.2.1`.

# 1.2.0

- Added direct member registration by replying to a user with `/addmember`, `/member`, `ثبت عضو`, `عضو کن`, or `ثبتش کن`.
- Added `/debtors` and Persian aliases for a dedicated debtor list in the group.
- Added `/commands` for an in-group administrator command guide.
- Added `/mydebt` and Persian aliases for personal bill retrieval.
- After every successful payment, the bot now sends the payer confirmation, remaining debt, and a group announcement containing the updated fund balance.
- Separated the private invoice chat from the configured announcement group.
- Updated `run.ps1` to remove stale `late-fee-box` containers without deleting the persistent Docker volume.
- Replaced obsolete `KnownNetworks` usage with `KnownIPNetworks`.
- Updated the application image tag to `latefeebox-stable:1.2.0`.

# 1.1.1

- Fixed Windows PowerShell 5.1 parsing failures caused by UTF-8 scripts without BOM.
- Rewrote operational PowerShell scripts using ASCII-only text.
- Added run.cmd, test.cmd, and stop.cmd wrappers.
- Added explicit Docker Compose .env loading.
- Improved health-check wait and error handling.

# تغییرات کیف‌تاخیر v1.1.0

- بازطراحی کامل پنل با تم سبز عمیق، کرم گرم، طلایی و مرجانی.
- افزودن نام تجاری «کیف‌تاخیر» و لوگوی برداری اختصاصی.
- صفحه ورود، داشبورد، اعضا، کاربران دیده‌شده، صندوق، پرداخت‌ها و تنظیمات کاملاً بازطراحی شدند.
- افزودن صورت‌حساب شخصی کاربر با `/bill`، `/invoice`، `/pay` و عبارت‌های فارسی.
- ارسال کارت پرداخت در گفت‌وگوی خصوصی کاربر.
- بررسی عضویت فعلی کاربر در گروه حتی برای اعضای قبلاً ثبت‌شده.
- غیرفعال‌سازی عضو در صورت خروج از گروه.
- اضافه‌شدن راهنمای خصوصی بات و منوی فارسی.
- جلوگیری از ثبت مجدد پرداخت با هر دو شناسه تراکنش provider و telegram.
- پذیرش پرداخت موفق فقط پس از تأیید `pre_checkout_query`.
- منقضی‌کردن فاکتورهای Pending و Approved در صورت تغییر بدهی.
- حفظ Build آفلاین، Long Polling پایدار و ذخیره‌سازی JSON اتمیک.

## 1.1.2

- Removed the incorrect requirement that official .NET base images must appear in `docker image inspect`.
- The build now uses Docker/BuildKit cache directly and can reuse cached base layers.
- Added automatic fallback pulls for the .NET SDK and ASP.NET Runtime images.
- Fixed PowerShell 5.1 native command error handling so missing images produce a controlled message instead of `NativeCommandError`.
- Preserved offline NuGet restore: application build steps run with `--network=none` and the project has no external package references.
- Updated the application image tag to `latefeebox-stable:1.1.2`.
