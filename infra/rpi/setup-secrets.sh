#!/usr/bin/env bash
# Creates the database and the configuration of the API with generated secrets. Safe to run again: a value
# that already exists is kept, so the existing database and images keep working. It never prints a secret.
# Usage: sudo GOOGLE_WEB_CLIENT_ID=... bash setup-secrets.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ENV_FILE=/etc/geowoof/geowoof.env
S3_FILE=/etc/seaweedfs/s3.json

[ "$(id -u)" -eq 0 ] || { echo "Run it with sudo." >&2; exit 1; }

current() { sed -n "s/^$1=//p" "$ENV_FILE" 2>/dev/null | head -1 || true; }
generated() { openssl rand -base64 "$1" | tr -d '/+=\n'; }

jwt_key="$(current Auth__Jwt__SigningKey)"; jwt_key="${jwt_key:-$(generated 48)}"
s3_access="$(current Storage__AccessKey)"; s3_access="${s3_access:-$(generated 15)}"
s3_secret="$(current Storage__SecretKey)"; s3_secret="${s3_secret:-$(generated 36)}"
google_id="${GOOGLE_WEB_CLIENT_ID:-$(current Auth__Google__ClientIds__0)}"
smtp_key="$(current Email__SmtpPassword)"

umask 027
sed -e "s|^Auth__Jwt__SigningKey=.*|Auth__Jwt__SigningKey=$jwt_key|" \
    -e "s|^Auth__Google__ClientIds__0=.*|Auth__Google__ClientIds__0=$google_id|" \
    -e "s|^Storage__AccessKey=.*|Storage__AccessKey=$s3_access|" \
    -e "s|^Storage__SecretKey=.*|Storage__SecretKey=$s3_secret|" \
    -e "s|^Email__SmtpPassword=.*|Email__SmtpPassword=$smtp_key|" \
    -e '/^#/d' "$HERE/geowoof.env.example" > "$ENV_FILE.new"
chown root:geowoof "$ENV_FILE.new"
chmod 640 "$ENV_FILE.new"
mv "$ENV_FILE.new" "$ENV_FILE"

cat > "$S3_FILE.new" <<EOF
{
  "identities": [
    {
      "name": "geowoof",
      "credentials": [{ "accessKey": "$s3_access", "secretKey": "$s3_secret" }],
      "actions": ["Admin", "Read", "Write", "List", "Tagging"]
    }
  ]
}
EOF
chown root:seaweedfs "$S3_FILE.new"
chmod 640 "$S3_FILE.new"
mv "$S3_FILE.new" "$S3_FILE"

# The API connects through the local socket with the name of its system user (peer), without a password.
runuser -u postgres -- psql -qtAc "SELECT 1 FROM pg_roles WHERE rolname='geowoof'" | grep -q 1 \
  || runuser -u postgres -- psql -qc "CREATE ROLE geowoof LOGIN"
runuser -u postgres -- psql -qtAc "SELECT 1 FROM pg_database WHERE datname='geowoof'" | grep -q 1 \
  || runuser -u postgres -- psql -qc "CREATE DATABASE geowoof OWNER geowoof"
# PostGIS can only be created by a superuser. The migrations then find these extensions already there.
runuser -u postgres -- psql -q -d geowoof -c "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS unaccent;" 2>&1 | grep -v 'already exists' || true

systemctl restart seaweedfs
echo "Secrets ready: $ENV_FILE and $S3_FILE"
