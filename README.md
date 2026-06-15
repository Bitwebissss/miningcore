[![license](https://img.shields.io/github/license/mashape/apistatus.svg)]()
[![.NET](https://github.com/bitweb-project/miningcore/actions/workflows/dotnet.yml/badge.svg?branch=dev)](https://github.com/bitweb-project/miningcore/actions/workflows/dotnet.yml)

<img src="https://github.com/bitweb-project/miningcore/blob/dev/logo.svg" width="150">

### Features

- Supports clusters of pools each running individual currencies
- Ultra-low-latency, multi-threaded Stratum implementation using asynchronous I/O
- Adaptive share difficulty ("vardiff")
- PoW validation (hashing) using native code for maximum performance
- Session management for purging DDoS/flood initiated zombie workers
- Payment processing
- Banning System
- Live Stats [API](https://pool.bitwebcore.net/api)
- WebSocket streaming of notable events like Blocks found, Blocks unlocked, Payments and more
- Detailed per-pool logging to console & filesystem
- Runs on Linux and Windows

## Contributions

Code contributions are very welcome and should be submitted as standard [pull requests](https://docs.github.com/en/pull-requests) (PR) based on the [`dev` branch](https://github.com/bitweb-project/miningcore/tree/dev).

## Building on Ubuntu 24.04

```console
git clone https://github.com/bitweb-project/miningcore
cd miningcore
./build-ubuntu-24_04.sh
```

## Building on Ubuntu 22.04

```console
git clone https://github.com/bitweb-project/miningcore
cd miningcore
./build-ubuntu-22.04.sh
```

## Building on Debian 12

```console
git clone https://github.com/bitweb-project/miningcore
cd miningcore
./build-debian-12.sh
```

All build scripts install **dotnet-sdk-10.0** and the required system libraries, then run:

```console
dotnet publish -c Release --framework net10.0 -o ../../build
```

An optional output path can be passed as the first argument:

```console
./build-ubuntu-24_04.sh /opt/miningcore
```

## Building on Windows

**Step 1 — build the native crypto library (you can skip this stage and use exist)**

Install choco

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
```

Install vs build tools.

```console
choco install visualstudio2026buildtools -y
choco install visualstudio2026-workload-vctools -y
```

```
src\Native\libmultihash\x64\Release\libmultihash.dll
```

Create the target directory and copy the DLL there:

```dosbatch
mkdir libs\runtimes\win-x64
copy src\Native\libmultihash\x64\Release\libmultihash.dll libs\runtimes\win-x64\
```


Download and install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)


**Step 2 — build the managed project**

```dosbatch
git clone https://github.com/bitweb-project/miningcore
cd miningcore
build-windows.bat
```

The final binaries are placed in the `build\` directory.


## Building using Docker

Docker builds always produce a **Linux** executable.

```console
git clone https://github.com/bitweb-project/miningcore
cd miningcore
```

Linux build:

```console
docker run --rm -v $(pwd):/app -w /app mcr.microsoft.com/dotnet/sdk:10.0 \
  /bin/bash -c 'apt-get update && apt-get install -y cmake ninja-build build-essential \
  libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5-dev libgmp-dev \
  && cd src/Miningcore && dotnet publish -c Release --framework net10.0 -o /app/build/'
```

### Building and running from a container

> **Note** — Build scripts optimise the binary for the CPU of the build host (AVX, SSE, etc.). Always build the container on the host you intend to run it on.

```console
docker build -t <your_dockerhubid>/miningcore:latest .
```

```sh
docker run -d \
    -p 4000:4000 \
    -p 4066:4066 \
    -p 4067:4067 \
    --name mc \
    -v $(pwd)/config.json:/app/config.json \
    --restart=unless-stopped \
    <your_dockerhubid>/miningcore:latest
```

```console
docker system prune -af
```

## Running Miningcore

### Production OS

Linux and Windows (x64) are both supported for production use.

### Database setup

Miningcore requires PostgreSQL 11 or higher.

Run Postgres's `psql` tool:

```console
sudo -u postgres psql
```

In `psql` execute:

```sql
CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'your-secure-password';
CREATE DATABASE miningcore OWNER miningcore;
```

Quit `psql` with `\q`.

Import the database schema:

```console
sudo -u postgres psql -d miningcore -f miningcore/src/Miningcore/Persistence/Postgres/Scripts/createdb.sql
```

#### Create a partition for each pool

The `shares` table is partitioned by `poolid`. You must create one partition per pool before starting the pool (replace `mypool1` with the actual pool id from your config):

```sql
CREATE TABLE shares_mypool1 PARTITION OF shares FOR VALUES IN ('mypool1');
```

Repeat for each additional pool.

### Configuration

Create a configuration file `config.json`. Sample configs for all supported coins can be found in the [`examples/`](examples/) directory.

### Start the Pool

```console
cd build
./Miningcore -c config.json
```

On Windows:

```dosbatch
cd build
Miningcore.exe -c config.json
```

Test
```dosbatch
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -c Release --logger "console;verbosity=normal"
```

## Supported Currencies

Only **Bitcoin-family** (Stratum) coins are supported. All other families (Monero/RandomX, Ethereum, ZCash Equihash, etc.) have been removed.

### Bitweb (BTE) — Argon2id

The primary coin this pool software is built for.

- Algorithm: Argon2id (t=3, m=1024) — custom optimised implementation with SSE2 / SSSE3 / AVX2 / AVX-512 dispatch
- Website: https://bitwebcore.net
- Explorer: https://explorer.bitwebcore.net
- GitHub: https://github.com/bitweb-project/bitweb

Sample config: [`examples/bitweb_pool.json`](examples/bitweb_pool.json)

### SHA-256d

Bitcoin (BTC), Bitcoin Cash (BCH)...

### Scrypt

Litecoin (LTC), Dogecoin (DOGE)...

### Argon2ID

Bitweb (BTE)...

## API

Miningcore includes an integrated REST API. See: https://pool.bitwebcore.net/api

## Running a production pool

A public production pool needs a web frontend. you can use this https://github.com/bitweb-project/miningcore-ui 

## Extra fast Linux setup example what we use ( used ubuntu-24_04 ) 

create user pool

then login to root or use sudo -s

```console
apt-get update && apt-get upgrade
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get -y install dotnet-sdk-10.0 nano git cmake ninja-build build-essential libssl-dev pkg-config libzmq5-dev libgmp-dev nginx python3-certbot-dns-cloudflare gnupg curl postgresql python3 python3-venv python3-dev python3-pip python3-setuptools python3-multidict ufw
```

Setup certicifates ( you need cloudflare account domain)

```console
nano /etc/letsencrypt/cloudflare.ini
dns_cloudflare_api_token = token
chmod 600 /etc/letsencrypt/cloudflare.ini

mkdir -p /etc/letsencrypt/renewal-hooks/deploy
nano /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh

put
#!/bin/bash
systemctl reload nginx

save

cat /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh

chmod +x /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh
```

```console
certbot certonly --dns-cloudflare --dns-cloudflare-credentials /etc/letsencrypt/cloudflare.ini --dns-cloudflare-propagation-seconds 60 --force-renewal -d pool-api.example.net
```

```console
nano /etc/nginx/ngserver-global.conf

# Cloudflare real IP restoration
set_real_ip_from 103.21.244.0/22;
set_real_ip_from 103.22.200.0/22;
set_real_ip_from 104.16.0.0/13;
set_real_ip_from 104.24.0.0/14;
set_real_ip_from 108.162.192.0/18;
set_real_ip_from 141.101.64.0/18;
set_real_ip_from 162.158.0.0/15;
set_real_ip_from 172.64.0.0/13;
set_real_ip_from 173.245.48.0/20;
set_real_ip_from 188.114.96.0/20;
set_real_ip_from 190.93.240.0/20;
set_real_ip_from 197.234.240.0/22;
set_real_ip_from 198.41.128.0/17;
set_real_ip_from 131.0.72.0/22;
real_ip_header   CF-Connecting-IP;
real_ip_recursive on;

# Global IP blacklist (override in server blocks with another geo if needed)
geo $blocked_ip {
    default 0;
    # 1.2.3.4 1;
}

# Global User-Agent blacklist
map $http_user_agent $blocked_agent {
    default 0;
    ~*(sqlmap|nmap|nikto|burp|scanner) 1;
}

# Global malicious query blacklist
map $args $blocked_args {
    default 0;
    ~*(union.*select|drop.*table|insert.*into|\.\./|etc/passwd) 1;
}
```
```console
nano /opt/update-cf-ips.sh
```


```console
#!/bin/bash

set -euo pipefail

CF_IPV4="https://www.cloudflare.com/ips-v4"
CF_IPV6="https://www.cloudflare.com/ips-v6"

# Actual path nginx includes (conf.d/*.conf)
TARGET="/etc/nginx/conf.d/ngserver-global.conf"

# Temp file in the same directory — keeps cp atomic-ish (same filesystem)
TEMP="$(mktemp "${TARGET}.XXXXXX")"
BACKUP="$(mktemp "${TARGET}.bak.XXXXXX")"

cleanup() {
    rm -f "$TEMP" "$BACKUP"
}
trap cleanup EXIT

fail() {
    echo "$(date -u '+%Y-%m-%d %H:%M UTC') ERROR: $*" >&2
    exit 1
}

fetch_ips() {
    local url="$1"
    curl -fsSL --max-time 10 "$url"
}

# --- Step 1: fetch IP lists, explicitly checking each curl ---
ipv4_list=$(fetch_ips "$CF_IPV4") || fail "failed to fetch $CF_IPV4"
ipv6_list=$(fetch_ips "$CF_IPV6") || fail "failed to fetch $CF_IPV6"

# Sanity check: lists must not be empty or suspiciously short
ipv4_count=$(printf '%s\n' "$ipv4_list" | grep -c '/' || true)
ipv6_count=$(printf '%s\n' "$ipv6_list" | grep -c '/' || true)

if [ "$ipv4_count" -lt 5 ] || [ "$ipv6_count" -lt 1 ]; then
    fail "suspiciously few IP ranges (v4=$ipv4_count v6=$ipv6_count) — not applying, keeping current config"
fi

# --- Step 2: generate the new config ---
generate_config() {
    echo "# Cloudflare IP ranges — auto-updated $(date -u '+%Y-%m-%d %H:%M UTC')"
    echo "# Do not edit manually — managed by update-cf-ips.sh"
    echo ""

    printf '%s\n' "$ipv4_list" | while IFS= read -r ip; do
        [ -n "$ip" ] && echo "set_real_ip_from ${ip};"
    done

    printf '%s\n' "$ipv6_list" | while IFS= read -r ip; do
        [ -n "$ip" ] && echo "set_real_ip_from ${ip};"
    done

    echo "real_ip_header   CF-Connecting-IP;"
    echo "real_ip_recursive on;"
    echo ""
    cat << 'STATIC'
geo $blocked_ip {
    default 0;
    # 1.2.3.4 1;
}

map $http_user_agent $blocked_agent {
    default 0;
    ~*(sqlmap|nmap|nikto|burp|scanner) 1;
}

map $args $blocked_args {
    default 0;
    ~*(union.*select|drop.*table|insert.*into|\.\./|etc/passwd) 1;
}
STATIC
}

generate_config > "$TEMP"

# --- Step 3: compare, ignoring the timestamp line (otherwise it always "differs") ---
if [ -f "$TARGET" ]; then
    if diff -q <(tail -n +2 "$TARGET") <(tail -n +2 "$TEMP") > /dev/null 2>&1; then
        # Content (without the date line) unchanged — nothing to do
        exit 0
    fi
    cp "$TARGET" "$BACKUP"
else
    : > "$BACKUP"  # empty backup = "no previous file"
fi

# --- Step 4: apply the new config and validate it ---
cp "$TEMP" "$TARGET"

if ! nginx -t 2>/tmp/nginx-test-err; then
    # Roll back so we never leave a broken config on disk
    if [ -s "$BACKUP" ]; then
        cp "$BACKUP" "$TARGET"
    else
        rm -f "$TARGET"
    fi
    fail "nginx -t failed on the new config, rolled back: $(cat /tmp/nginx-test-err)"
fi

nginx -s reload
echo "$(date -u '+%Y-%m-%d %H:%M UTC') Cloudflare IPs updated and nginx reloaded (v4=$ipv4_count v6=$ipv6_count)"
```

```console

nano /etc/logrotate.d/update-cf-ips

put
/var/log/update-cf-ips.log {
    weekly
    rotate 4
    compress
    missingok
    notifempty
}

save

chown root:root /opt/update-cf-ips.sh
chmod 700 /opt/update-cf-ips.sh

crontab -e

add
17 3 * * * /opt/update-cf-ips.sh >> /var/log/update-cf-ips.log 2>&1

nano /etc/logrotate.d/update-cf-ips

add

/var/log/update-cf-ips.log {
    weekly
    rotate 4
    compress
    missingok
    notifempty
    su root root
}

save

```

```console

nano /etc/logrotate.d/miningcore

put

/home/pool/miningcore/log/miningcore.log {
    daily
    rotate 7
    compress
    missingok
    notifempty
    copytruncate
    su pool pool
}

save

```

Test
```console
logrotate -vf /etc/logrotate.d/update-cf-ips
logrotate -vf /etc/logrotate.d/miningcore
```

```console
nano /etc/nginx/sites-available/pool-api.example.net

put

limit_req_zone $binary_remote_addr zone=api_pool:10m   rate=60r/m;
limit_req_zone $binary_remote_addr zone=api_miner:10m  rate=120r/m;
limit_req_zone $binary_remote_addr zone=api_ws:10m     rate=20r/m;
limit_req_zone $binary_remote_addr zone=api_misc:10m   rate=20r/m;
limit_conn_zone $binary_remote_addr zone=ws_conn:10m;

server {
    listen 80;
    server_name pool-api.example.net;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    server_name pool-api.example.net;

    ssl_certificate     /etc/letsencrypt/live/pool-api.example.net/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/pool-api.example.net/privkey.pem;

    server_tokens off;

    add_header X-Content-Type-Options    "nosniff"                                  always;
    add_header X-Frame-Options           "DENY"                                     always;
    add_header X-XSS-Protection          "1; mode=block"                            always;
    add_header Referrer-Policy           "no-referrer"                              always;
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains"      always;
    add_header Permissions-Policy        "camera=(), microphone=(), geolocation=()" always;

    add_header Access-Control-Allow-Origin  "*"                  always;
    add_header Access-Control-Allow-Methods "GET, POST, OPTIONS" always;
    add_header Access-Control-Allow-Headers "Content-Type"       always;

    if ($request_method = OPTIONS) {
        return 204;
    }

    limit_req_status 429;
    limit_req_log_level warn;

    location = /notifications {
        limit_req          zone=api_ws burst=10 nodelay;
        limit_conn         ws_conn 10;
        proxy_pass         http://127.0.0.1:4000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade           $http_upgrade;
        proxy_set_header   Connection        "upgrade";
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_read_timeout 3600s;
    }

    location = /api/health-check {
        limit_req          zone=api_misc burst=10 nodelay;
        proxy_pass         http://127.0.0.1:4000;
        proxy_set_header   Host $host;
        proxy_read_timeout 5s;
    }

    location = /api/help {
        limit_req          zone=api_misc burst=5 nodelay;
        proxy_pass         http://127.0.0.1:4000;
        proxy_set_header   Host $host;
    }

    location = /api/pools-list {
        limit_req          zone=api_pool burst=20 nodelay;
        proxy_pass         http://127.0.0.1:4000;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }

    location ~ "^/api/pools/[a-zA-Z0-9_-]+(/performance|/blocks)?$" {
        limit_req          zone=api_pool burst=20 nodelay;
        proxy_pass         http://127.0.0.1:4000;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }

    location ~ "^/api/v2/pools/[a-zA-Z0-9_-]+/blocks$" {
        limit_req          zone=api_pool burst=20 nodelay;
        proxy_pass         http://127.0.0.1:4000;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }

    location ~ "^/api/pools/[a-zA-Z0-9_-]+/miners/[a-zA-Z0-9]{25,90}(/blocks|/payments|/settings)?$" {
        limit_req          zone=api_miner burst=40 nodelay;
        proxy_pass         http://127.0.0.1:4000;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 20s;
    }

    location ~ "^/api/v2/pools/[a-zA-Z0-9_-]+/miners/[a-zA-Z0-9]{25,90}/(blocks|payments)$" {
        limit_req          zone=api_miner burst=40 nodelay;
        proxy_pass         http://127.0.0.1:4000;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 20s;
    }

    location / {
        return 444;
    }
}

```

```console

# /etc/nginx/sites-available/default-drop
server {
    listen 80 default_server;
    listen 443 ssl default_server;
    
    ssl_certificate     /etc/letsencrypt/live/pool-api.example.net/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/pool-api.example.net/privkey.pem;
    
    server_name _;
    
    return 444;
}

```

```console
ln -s /etc/nginx/sites-available/pool.bitwebcore.net /etc/nginx/sites-enabled/pool.bitwebcore.net
ln -s /etc/nginx/sites-available/default-drop /etc/nginx/sites-enabled/default-drop
systemctl restart nginx
```

```console
nano /etc/postgresql/16/main/conf.d/tuning.conf

put

# MEMORY
shared_buffers = 1GB
work_mem = 6MB
maintenance_work_mem = 128MB
wal_buffers = 32MB

# WAL
min_wal_size = 512MB
max_wal_size = 2048MB
wal_compression = on

# PARALLEL
max_worker_processes = 4
max_parallel_workers = 4

# SSD
random_page_cost = 1.1
effective_io_concurrency = 200
maintenance_io_concurrency = 100

# AUTOVACUUM
autovacuum_vacuum_scale_factor = 0.05
autovacuum_analyze_scale_factor = 0.02
autovacuum_vacuum_insert_scale_factor = 0.02

# LOGGING
log_min_duration_statement = 500
log_lock_waits = on
deadlock_timeout = 500ms

save

pg_ctlcluster 16 main restart
```

```console
nano /etc/systemd/system/enable-transparent-huge-pages.service

put

[Unit]
Description=Set Transparent Hugepages (THP)
DefaultDependencies=no
After=sysinit.target local-fs.target
Before=mongod.service postgresql.service
[Service]
Type=oneshot
ExecStart=/bin/sh -c 'echo madvise > /sys/kernel/mm/transparent_hugepage/enabled && echo defer+madvise > /sys/kernel/mm/transparent_hugepage/defrag && echo 0 > /sys/kernel/mm/transparent_hugepage/khugepaged/max_ptes_none'
[Install]
WantedBy=multi-user.target

save

systemctl daemon-reload
systemctl enable enable-transparent-huge-pages
systemctl start enable-transparent-huge-pages

check

cat /sys/kernel/mm/transparent_hugepage/enabled

always [madvise] never

cat /sys/kernel/mm/transparent_hugepage/defrag

always defer [defer+madvise] madvise never

cat /sys/kernel/mm/transparent_hugepage/khugepaged/max_ptes_none

0
```

```console
ufw status
ufw allow ssh
ufw allow http
ufw allow https
ufw allow 26333
ufw allow 3032
ufw allow 3033

ufw enable
ufw status

26333 - node p2p port 

3032 PPLNS mining port
3033 SOLO mining port
```

```console
sudo nano /etc/systemd/system/miningcore.service

[Unit]
Description=Miningcore Pool
After=network.target postgresql.service

[Service]
Type=simple
User=pool
WorkingDirectory=/home/pool/miningcore/build
ExecStart=/home/pool/miningcore/build/Miningcore -c bitweb_pool.json
Restart=no
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
```

```console
sudo systemctl daemon-reload
sudo systemctl enable miningcore

not start now!
```


```sql
sudo -u postgres psql
CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'strongpassword';
CREATE DATABASE miningcore OWNER miningcore;
\q

dont forgot save some where password you will need it!
```

now return to pool user exit or if you use different users login to pool user at your server.

download node and sutup it WITH

bitweb.conf example 

```console
server=1
rpcuser=user
rpcpassword=password
rpcallowip=127.0.0.1
rpcport=26332
wallet=pool
txindex=1
coinstatsindex=1
server=1
listen=1
daemon=1
zmqpubhashblock=tcp://127.0.0.1:28332
rpcworkqueue=512
rpcthreads=32
rpcclienttimeout=30
maxconnections=150
maxuploadtarget=2000
addnode=43.138.48.57
dbcache=2048
maxmempool=300
```

start node and go to

```console

cd bitweb/bin

./bitweb-cli createwallet "pool"
./bitweb-cli getnewaddress "" bech32m
it will return web1q...
save it.

./bitweb-cli encryptwallet "strongwalletpassword"

save also.
```


```console
git clone https://github.com/bitweb-project/miningcore
cd miningcore
psql -h 127.0.0.1 -U miningcore -d miningcore < ~/miningcore/src/Miningcore/Persistence/Postgres/Scripts/createdb.sql
```
write password and enter.

```console
psql -h 127.0.0.1 -U miningcore -d miningcore -c "CREATE TABLE shares_bte1 PARTITION OF shares FOR VALUES IN ('bte1');"
psql -h 127.0.0.1 -U miningcore -d miningcore -c "CREATE TABLE shares_bte1solo PARTITION OF shares FOR VALUES IN ('bte1solo');"
```

check 10 tables.

```console
psql -h 127.0.0.1 -U miningcore -d miningcore -c "\dt"
```

```console
bash build-ubuntu-24_04.sh

mkdir -p /home/pool/miningcore/ssl

openssl req -x509 -newkey rsa:4096 -keyout key.pem -out cert.pem \
  -days 36500 -nodes -subj "/CN=pool"

openssl pkcs12 -export \
  -out /home/pool/miningcore/ssl/pool.pfx \
  -inkey key.pem \
  -in cert.pem \
  -passout pass:

rm key.pem cert.pem


openssl pkcs12 -in /home/pool/miningcore/ssl/pool.pfx -noout -passin pass: && echo "OK"
```

```console
cp example bitweb_pool.json /home/pool/build/poolconfig.json
cd build 
nano poolconfig.json
```

edit settings, put address / password etc.

save

login as root and start pool

```console

sudo systemctl start miningcore
sudo systemctl status miningcore
sudo systemctl stop miningcore
```

admin script

```console
python3 mcadmin.py
```

restore shares

```console
./Miningcore -c bitweb_pool.json -rs recovered-shares.txt
```

check logs

```console
tail -f /home/pool/miningcore/log/miningcore.log

tail -f /home/pool/miningcore/log/bte1.log

tail -f /home/pool/miningcore/log/bte1solo.log
```

go to cloud flare and c reate separate domain for mining

for example our mining.bitwebcore.net and pin to server.

if pool sucess run, not you can go to step for setup UI.

https://github.com/bitweb-project/miningcore-ui


Your steps may differ depending on whether you're using this guide as a basic example. Security settings and other overlooked details are your responsibility. Adaptation to your environment is your responsibility.
