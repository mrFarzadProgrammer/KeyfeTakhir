# راهنمای استقرار دائمی کیف‌تاخیر روی سرور Ubuntu

این روش برای Ubuntu 24.04 یا 22.04، Docker Engine و Long Polling نوشته شده است. خود بات به دامنه و Webhook نیاز ندارد. پنل مدیریت به‌صورت پیش‌فرض فقط روی `127.0.0.1:8080` سرور باز می‌شود تا عمومی و ناامن نباشد.

## 1. مشخصات پیشنهادی سرور

- Ubuntu 24.04 LTS یا 22.04 LTS
- حداقل 1 vCPU
- حداقل 1 GB RAM
- حداقل 10 GB فضای دیسک
- دسترسی SSH با کاربر دارای sudo
- دسترسی خروجی HTTPS به `tapi.bale.ai` و رجیستری Microsoft Container

## 2. اتصال به سرور

از PowerShell ویندوز:

```powershell
ssh root@SERVER_IP
```

بهتر است بعداً به‌جای root از یک کاربر sudo استفاده شود.

## 3. نصب Docker Engine و Compose

روی سرور:

```bash
sudo apt update
sudo apt install -y ca-certificates curl unzip
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

sudo tee /etc/apt/sources.list.d/docker.sources >/dev/null <<EOF_DOCKER
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}")
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF_DOCKER

sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
sudo docker version
sudo docker compose version
```

## 4. ساخت پوشه برنامه

```bash
sudo mkdir -p /opt/keyfetakhir
sudo chown -R "$USER":"$USER" /opt/keyfetakhir
```

## 5. انتقال فایل از ویندوز

در PowerShell ویندوز و از پوشه‌ای که ZIP قرار دارد:

```powershell
scp .\KeyfeTakhir-v1.2.1.zip root@SERVER_IP:/opt/keyfetakhir/
```

فایل `.env` را نیز منتقل کن:

```powershell
scp .\.env root@SERVER_IP:/opt/keyfetakhir/
```

در صورت استفاده از کاربر غیر root، نام همان کاربر را جایگزین کن. WinSCP نیز برای انتقال گرافیکی مناسب است.

## 6. Extract و آماده‌سازی

روی سرور:

```bash
cd /opt/keyfetakhir
unzip -o KeyfeTakhir-v1.2.1.zip
cd KeyfeTakhir-v1.2.1
mv ../.env ./.env
chmod 600 .env
chmod +x deploy-server.sh backup-server.sh server-status.sh
```

کنترل کن که `.env` شامل مقادیر صحیح باشد؛ مقادیر محرمانه را داخل چت یا لاگ عمومی منتشر نکن.

## 7. اولین Deploy

```bash
sudo ./deploy-server.sh
```

بعد از موفقیت:

```text
KeyfeTakhir started successfully.
Local admin endpoint: http://127.0.0.1:8080/admin/
```

## 8. بررسی وضعیت

```bash
sudo ./server-status.sh
```

یا جداگانه:

```bash
sudo docker compose -f docker-compose.server.yml --env-file .env ps
curl http://127.0.0.1:8080/health
sudo docker compose -f docker-compose.server.yml --env-file .env logs -f --tail 100 late-fee-box
```

## 9. دسترسی امن به پنل بدون دامنه

در PowerShell ویندوز این اتصال را باز نگه دار:

```powershell
ssh -L 8080:127.0.0.1:8080 root@SERVER_IP
```

سپس در مرورگر ویندوز باز کن:

```text
http://localhost:8080/admin/
```

به این روش پورت پنل در اینترنت عمومی باز نمی‌شود.

## 10. دسترسی دائمی با دامنه و HTTPS

ابتدا یک Subdomain مثل `panel.example.com` را به IP سرور متصل کن. سپس روی سرور:

```bash
sudo apt update
sudo apt install -y nginx snapd
sudo systemctl enable --now nginx
```

فایل Nginx بساز:

```bash
sudo nano /etc/nginx/sites-available/keyfetakhir
```

محتوا:

```nginx
server {
    listen 80;
    listen [::]:80;
    server_name panel.example.com;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

فعال‌سازی:

```bash
sudo ln -s /etc/nginx/sites-available/keyfetakhir /etc/nginx/sites-enabled/keyfetakhir
sudo nginx -t
sudo systemctl reload nginx
```

نصب Certbot با Snap و فعال‌سازی HTTPS:

```bash
sudo snap install --classic certbot
sudo ln -s /snap/bin/certbot /usr/local/bin/certbot
sudo certbot --nginx -d panel.example.com
sudo certbot renew --dry-run
```

پس از آن پنل از طریق `https://panel.example.com/admin/` در دسترس است.

## 11. ماندگاری بعد از Restart سرور

Compose از `restart: unless-stopped` استفاده می‌کند و Docker نیز به‌عنوان سرویس سیستم فعال می‌شود. برای آزمایش:

```bash
sudo reboot
```

پس از اتصال مجدد:

```bash
cd /opt/keyfetakhir/KeyfeTakhir-v1.2.1
sudo ./server-status.sh
```

## 12. Backup اطلاعات

```bash
cd /opt/keyfetakhir/KeyfeTakhir-v1.2.1
sudo ./backup-server.sh
```

فایل Backup در پوشه `backups` ساخته می‌شود. آن را به سیستم شخصی منتقل کن:

```powershell
scp root@SERVER_IP:/opt/keyfetakhir/KeyfeTakhir-v1.2.1/backups/keyfetakhir-*.tar.gz .\
```

## 13. آپدیت نسخه‌های بعدی

قبل از Update:

```bash
sudo ./backup-server.sh
```

نسخه جدید را در پوشه جدید Extract کن، `.env` را از نسخه قبلی کپی کن و `deploy-server.sh` را اجرا کن. Volume با نام `late_fee_box_stable_data` حذف نمی‌شود و اطلاعات باقی می‌ماند.

هیچ‌وقت برای Update این دستور را با گزینه `-v` اجرا نکن:

```bash
docker compose down -v
```

زیرا `-v` باعث حذف Volume اطلاعات می‌شود.

## 14. تست فرمان جدید جریمه

داخل گروه، مدیر روی پیام عضو Reply می‌زند و می‌فرستد:

```text
/fine200
```

باید ۲۰۰,۰۰۰ تومان به بدهی همان عضو اضافه شود. سپس با `/debtors` و پنل، مبلغ را کنترل کن.
