#!/usr/bin/env bash
# Publishes on this Raspberry Pi the latest release of the API on GitHub, once its package for the Raspberry Pi is
# there: a systemd timer runs it every few minutes. It asks the public API of GitHub, with no credential, and only
# acts on a release it has not handled before, so a version published from a computer with deploy.ps1 stays until
# the next release. The steps are those of deploy.ps1: a copy of the database, the new version next to the others,
# a restart, and back to the previous version if it does not answer.
#
#   geowoof-auto-deploy              what the timer runs
#   geowoof-auto-deploy --check      says what it would do, and changes nothing
#   geowoof-auto-deploy --file PKG   publishes a package that is already on this Raspberry Pi
#
# With NOTIFY_EMAIL in /etc/geowoof/auto-deploy.env, it says how it went by email, with the SMTP of the API.
set -euo pipefail

REPO="${GEOWOOF_REPO:-eirikrg/geowoof-api}"
ASSET=geowoof-api-linux-arm64.tar.gz
STATE=/var/lib/geowoof-deploy
RELEASES=/opt/geowoof/releases
CURRENT=/opt/geowoof/current
KEEP_RELEASES=3
READY_SECONDS="${GEOWOOF_READY_SECONDS:-240}"
# deploy.ps1 leaves it while it publishes from a computer.
BUSY=/run/geowoof-deploy.busy

mode=timer
package_file=""
case "${1:-}" in
  "") ;;
  --check) mode=check ;;
  --file) mode=file; package_file="${2:?--file needs the path of a package}" ;;
  *) echo "Unknown option: $1" >&2; exit 2 ;;
esac

install -d -m 755 "$STATE"
exec 9> "$STATE/lock"
if ! flock -n 9; then
  echo "Another publication is running."
  exit 0
fi

env_value() { sed -n "s/^$1=//p" "$2" 2>/dev/null | tail -n 1; }

send_mail() {
  local subject="$1" body="$2" to host port security user password from name scheme
  to="$(env_value NOTIFY_EMAIL /etc/geowoof/auto-deploy.env)"
  host="$(env_value Email__SmtpHost /etc/geowoof/geowoof.env)"
  if [ -z "$to" ] || [ -z "$host" ]; then
    return 0
  fi
  port="$(env_value Email__SmtpPort /etc/geowoof/geowoof.env)"
  security="$(env_value Email__SmtpSecurity /etc/geowoof/geowoof.env)"
  user="$(env_value Email__SmtpUserName /etc/geowoof/geowoof.env)"
  password="$(env_value Email__SmtpPassword /etc/geowoof/geowoof.env)"
  from="$(env_value Email__FromAddress /etc/geowoof/geowoof.env)"
  name="$(env_value Email__FromName /etc/geowoof/geowoof.env)"
  scheme=smtp
  if [ "$security" = SslOnConnect ] || [ "${port:-587}" = 465 ]; then
    scheme=smtps
  fi
  # The password goes in a config on a file descriptor, so that it never shows in the list of processes.
  printf 'From: %s <%s>\r\nTo: %s\r\nSubject: %s\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n%s\r\n' \
    "${name:-GeoWoof}" "$from" "$to" "$subject" "$body" |
    curl --silent --show-error --max-time 30 --url "$scheme://$host:${port:-587}" --ssl-reqd \
      --mail-from "$from" --mail-rcpt "$to" --config <(printf 'user = "%s:%s"\n' "$user" "$password") \
      --upload-file - ||
    echo "Could not send the email." >&2
}

record() {
  if [ "$1" != local ]; then
    echo "$1" > "$STATE/release"
  fi
  printf '%s %s %s\n' "$(date -Is)" "$1" "$2" >> "$STATE/history"
}

work="$(mktemp -d /var/tmp/geowoof-deploy.XXXXXX)"
trap 'rm -rf "$work"' EXIT

if [ "$mode" = file ]; then
  tag=local
  cp "$package_file" "$work/$ASSET"
else
  answer="$(curl -fsS --max-time 30 -H 'Accept: application/vnd.github+json' \
    "https://api.github.com/repos/$REPO/releases/latest" || true)"
  if [ -z "$answer" ]; then
    echo "GitHub did not answer: next time."
    exit 0
  fi
  if ! read -r tag package checksum < <(printf '%s' "$answer" | python3 -c '
import json, sys
release = json.load(sys.stdin)
urls = {asset["name"]: asset["browser_download_url"] for asset in release.get("assets", [])}
print(release["tag_name"], urls.get(sys.argv[1], "-"), urls.get(sys.argv[1] + ".sha256", "-"))
' "$ASSET" 2>/dev/null); then
    echo "GitHub answered something unexpected: next time."
    exit 0
  fi

  if [ "$tag" = "$(cat "$STATE/release" 2>/dev/null || true)" ]; then
    if [ "$mode" = check ]; then
      echo "$tag is already published: nothing to do."
    fi
    exit 0
  fi
  if [ "$package" = - ] || [ "$checksum" = - ]; then
    echo "$tag has no package for the Raspberry Pi yet."
    exit 0
  fi
  if [ "$mode" = check ]; then
    echo "Would publish $tag, now on $(cat "$CURRENT/SOURCE" 2>/dev/null || readlink -f "$CURRENT")."
    exit 0
  fi
  if [ -e "$BUSY" ] || systemctl is-active --quiet geowoof-backup.service; then
    echo "A computer is publishing, or the nightly backup is running: next time."
    exit 0
  fi

  echo "== Downloading $tag"
  curl -fsSL --max-time 900 -o "$work/$ASSET" "$package"
  curl -fsSL --max-time 60 -o "$work/$ASSET.sha256" "$checksum"
  if ! (cd "$work" && sha256sum --check --status "$ASSET.sha256"); then
    record "$tag" corrupt
    send_mail "GeoWoof: $tag was not published" "Its package does not match its checksum. Nothing was changed."
    echo "The package of $tag does not match its checksum." >&2
    exit 1
  fi
fi

stamp="$(date +%Y%m%d-%H%M%S)"
target="$RELEASES/$stamp"
previous="$(readlink -f "$CURRENT" || true)"

echo "== Backup of the database before the migrations"
install -d -m 700 /var/backups/geowoof/pre-deploy
runuser -u postgres -- pg_dump --format=custom --dbname=geowoof > /var/backups/geowoof/pre-deploy/latest.dump

echo "== Installing $tag in $target"
install -d -m 755 "$target"
tar -xzf "$work/$ASSET" -C "$target" --no-same-owner
chown -R root:root "$target"
chmod -R u=rwX,go=rX "$target"
chmod 755 "$target/GeoWoof.Api"
ln -sfn "$target" "$CURRENT"
systemctl restart geowoof-api

echo "== Waiting for /health/ready (up to $READY_SECONDS s)"
ready=false
deadline=$((SECONDS + READY_SECONDS))
while [ "$SECONDS" -lt "$deadline" ]; do
  sleep 5
  if curl -fs --max-time 4 -o /dev/null http://127.0.0.1:8080/health/ready; then
    ready=true
    break
  fi
done

if [ "$ready" != true ]; then
  log="$(journalctl -u geowoof-api -n 30 --no-pager 2>&1 || true)"
  if [ -n "$previous" ] && [ -d "$previous" ] && [ "$previous" != "$target" ]; then
    ln -sfn "$previous" "$CURRENT"
    systemctl restart geowoof-api
    back="It went back to $(cat "$previous/SOURCE" 2>/dev/null || echo "$previous")."
  else
    back="There was no previous version to go back to."
  fi
  record "$tag" failed
  send_mail "GeoWoof: $tag did not start on the Raspberry Pi" \
    "$back If it applied migrations, the copy of the database from before is /var/backups/geowoof/pre-deploy/latest.dump.

$log"
  echo "$tag did not start. $back" >&2
  exit 1
fi

# Newest first: the one just published is always kept.
ls -1t "$RELEASES" | tail -n +$((KEEP_RELEASES + 1)) | while read -r old; do rm -rf "${RELEASES:?}/$old"; done
record "$tag" published
send_mail "GeoWoof: $tag is on the Raspberry Pi" "$(cat "$target/SOURCE" 2>/dev/null || echo "$tag") is answering."
echo "== $tag ready"
