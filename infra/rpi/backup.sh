#!/usr/bin/env bash
# Nightly copy of the database and the images. On the same microSD it protects from a mistake, not from a
# broken card: taking the copies out of the Raspberry Pi is still pending.
set -euo pipefail

DIR=/var/backups/geowoof
KEEP_DAYS=7
STAMP="$(date +%Y-%m-%d_%H%M)"
TEMP="$DIR/.$STAMP"

install -d -m 700 "$TEMP"
trap 'systemctl start seaweedfs || true; rm -rf "$TEMP"' EXIT

runuser -u postgres -- pg_dump --format=custom --dbname=geowoof > "$TEMP/geowoof.dump"
pg_restore --list "$TEMP/geowoof.dump" > /dev/null

# SeaweedFS stops for a moment, so that its files are consistent. The API retries what fails meanwhile.
systemctl stop seaweedfs
tar -C /var/lib -czf "$TEMP/seaweedfs.tar.gz" seaweedfs
systemctl start seaweedfs

( cd "$TEMP" && sha256sum geowoof.dump seaweedfs.tar.gz > SHA256SUMS )
mv "$TEMP" "$DIR/$STAMP"
trap - EXIT

find "$DIR" -mindepth 1 -maxdepth 1 -type d -name '20*' -mtime "+$KEEP_DAYS" -exec rm -rf {} +
echo "Backup $DIR/$STAMP: $(du -sh "$DIR/$STAMP" | cut -f1)"
