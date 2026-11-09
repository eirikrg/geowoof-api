#!/usr/bin/env bash
# Prepares a Raspberry Pi OS Lite (64-bit, Debian 13) for GeoWoof. Safe to run again: it only adds what is
# missing. Run from a copy of infra/rpi on the Raspberry Pi: sudo bash setup-system.sh
# It generates no secrets: that is setup-secrets.sh.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SEAWEEDFS_VERSION=4.52
LAN=192.168.1.0/24

[ "$(id -u)" -eq 0 ] || { echo "Run it with sudo." >&2; exit 1; }
[ "$(dpkg --print-architecture)" = arm64 ] || { echo "It needs a 64-bit system." >&2; exit 1; }

export DEBIAN_FRONTEND=noninteractive

echo "== Packages"
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
  ca-certificates curl gnupg libicu76 python3 unattended-upgrades ufw caddy redis-server

echo "== PostgreSQL 18 and PostGIS (PGDG repository: Debian 13 only has PostgreSQL 17)"
if [ ! -f /etc/apt/sources.list.d/pgdg.sources ]; then
  install -d /usr/share/postgresql-common/pgdg
  curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc
  cat > /etc/apt/sources.list.d/pgdg.sources <<'EOF'
Types: deb
URIs: https://apt.postgresql.org/pub/repos/apt
Suites: trixie-pgdg
Components: main
Architectures: arm64
Signed-By: /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
EOF
  apt-get update -qq
fi
apt-get install -y -qq --no-install-recommends postgresql-18 postgresql-18-postgis-3 postgresql-contrib
install -m 644 -o root -g root "$HERE/postgresql-geowoof.conf" /etc/postgresql/18/main/conf.d/geowoof.conf
systemctl restart postgresql
systemctl enable --now postgresql

echo "== Redis"
install -m 644 -o root -g root "$HERE/redis-geowoof.conf" /etc/redis/geowoof.conf
grep -qxF 'include /etc/redis/geowoof.conf' /etc/redis/redis.conf || echo 'include /etc/redis/geowoof.conf' >> /etc/redis/redis.conf
systemctl restart redis-server
systemctl enable --now redis-server

echo "== SeaweedFS $SEAWEEDFS_VERSION"
if ! /usr/local/bin/weed version 2>/dev/null | grep -q "$SEAWEEDFS_VERSION"; then
  temp="$(mktemp -d)"
  curl -fsSL -o "$temp/weed.tar.gz" "https://github.com/seaweedfs/seaweedfs/releases/download/$SEAWEEDFS_VERSION/linux_arm64.tar.gz"
  tar -xzf "$temp/weed.tar.gz" -C "$temp" weed
  install -m 755 -o root -g root "$temp/weed" /usr/local/bin/weed
  rm -rf "$temp"
fi
id seaweedfs >/dev/null 2>&1 || useradd --system --home-dir /var/lib/seaweedfs --shell /usr/sbin/nologin seaweedfs
install -d -m 750 -o seaweedfs -g seaweedfs /var/lib/seaweedfs
install -d -m 750 -o root -g seaweedfs /etc/seaweedfs
install -m 644 -o root -g root "$HERE/seaweedfs.service" /etc/systemd/system/seaweedfs.service

echo "== API"
id geowoof >/dev/null 2>&1 || useradd --system --home-dir /opt/geowoof --shell /usr/sbin/nologin geowoof
install -d -m 755 -o root -g root /opt/geowoof /opt/geowoof/releases
install -d -m 750 -o root -g geowoof /etc/geowoof
install -m 644 -o root -g root "$HERE/geowoof-api.service" /etc/systemd/system/geowoof-api.service
install -m 644 -o root -g root "$HERE/Caddyfile" /etc/caddy/Caddyfile
install -m 750 -o root -g root "$HERE/set-smtp-key.sh" /usr/local/sbin/geowoof-set-smtp-key

echo "== Backups"
install -d -m 750 -o root -g root /var/backups/geowoof
install -m 750 -o root -g root "$HERE/backup.sh" /usr/local/sbin/geowoof-backup
install -m 644 -o root -g root "$HERE/geowoof-backup.service" /etc/systemd/system/geowoof-backup.service
install -m 644 -o root -g root "$HERE/geowoof-backup.timer" /etc/systemd/system/geowoof-backup.timer

echo "== Each release of GitHub, published by itself"
install -d -m 755 -o root -g root /var/lib/geowoof-deploy
install -m 750 -o root -g root "$HERE/auto-deploy.sh" /usr/local/sbin/geowoof-auto-deploy
install -m 644 -o root -g root "$HERE/geowoof-auto-deploy.service" /etc/systemd/system/geowoof-auto-deploy.service
install -m 644 -o root -g root "$HERE/geowoof-auto-deploy.timer" /etc/systemd/system/geowoof-auto-deploy.timer

systemctl daemon-reload
systemctl enable --quiet seaweedfs geowoof-api
systemctl enable --quiet --now geowoof-backup.timer geowoof-auto-deploy.timer

echo "== Logs: limited, so that they do not wear the microSD or fill it"
install -d /etc/systemd/journald.conf.d
cat > /etc/systemd/journald.conf.d/geowoof.conf <<'EOF'
[Journal]
SystemMaxUse=100M
MaxRetentionSec=14day
EOF
systemctl restart systemd-journald

echo "== Memory limits of the services (the kernel of the Raspberry Pi has them off: it needs a reboot)"
CMDLINE=/boot/firmware/cmdline.txt
if ! grep -q 'cgroup_enable=memory' "$CMDLINE"; then
  cp -p "$CMDLINE" "$CMDLINE.before-geowoof"
  sed -i '1 s/$/ cgroup_enable=memory/' "$CMDLINE"
fi

echo "== Automatic security updates"
systemctl enable --quiet --now unattended-upgrades

echo "== Firewall: web for everybody, SSH only from home"
# Without a reset, so that the rules added on the Raspberry Pi itself, outside this project, stay.
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw allow from "$LAN" to any port 22 proto tcp comment 'SSH from home' >/dev/null
ufw allow 80/tcp comment 'HTTP: certificates and redirect' >/dev/null
ufw allow 443/tcp comment 'HTTPS' >/dev/null
ufw allow from "$LAN" to any port 5353 proto udp comment 'raspberrypi.local' >/dev/null
ufw --force enable >/dev/null
ufw status verbose | sed -n 1,20p

echo "== Done"
