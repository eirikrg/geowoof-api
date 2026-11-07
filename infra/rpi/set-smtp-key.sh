#!/usr/bin/env bash
# Saves the password of the SMTP provider and restarts the API. It asks for it, so that it is neither on the
# screen, nor in the command history, nor in the list of processes.
# Usage, from the PC: ssh -t pi@raspberrypi.local sudo geowoof-set-smtp-key
set -euo pipefail

ENV_FILE=/etc/geowoof/geowoof.env
[ "$(id -u)" -eq 0 ] || { echo "Run it with sudo." >&2; exit 1; }

read -rsp "SMTP key (it does not show while typing): " SMTP_KEY
echo
[ -n "$SMTP_KEY" ] || { echo "Nothing changed: the key is empty." >&2; exit 1; }

umask 027
SMTP_KEY="$SMTP_KEY" awk 'index($0, "Email__SmtpPassword=") == 1 { print "Email__SmtpPassword=" ENVIRON["SMTP_KEY"]; next } { print }' "$ENV_FILE" > "$ENV_FILE.new"
unset SMTP_KEY
chown root:geowoof "$ENV_FILE.new"
chmod 640 "$ENV_FILE.new"
mv "$ENV_FILE.new" "$ENV_FILE"

systemctl restart geowoof-api
echo "Saved. The API is restarting: it takes about 30 seconds."
