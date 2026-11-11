#!/bin/bash
# Set up ARI on a Debian host where Asterisk HTTP and ARI are unconfigured.
set -euo pipefail
cd "$(dirname "$0")"
[[ $EUID == 0 ]] || { echo "Run with sudo."; exit 1; }
for file in /etc/asterisk/{http,ari}.conf /etc/alxprgs-notify/asterisk.env; do
    [[ ! -e "$file" ]] || { echo "Existing $file. See docs/INTEGRATIONS.md for manual setup."; exit 1; }
done
LC_ALL=C asterisk -rx 'core show channels count' | grep -q '^0 active calls' ||
    { echo "Wait until the active Asterisk calls end."; exit 1; }
asterisk -rx 'pjsip show contacts'
read -rp 'Registered PJSIP endpoint name: ' endpoint
[[ $endpoint =~ ^[A-Za-z0-9_.-]+$ ]]
asterisk -rx "pjsip show endpoint $endpoint" |
    awk -v endpoint="$endpoint" '$1 == "Endpoint:" { split($2, name, "/"); if (name[1] == endpoint) found=1 } END { exit !found }' ||
    { echo "PJSIP endpoint not found: $endpoint"; exit 1; }
read -rsp 'Notify telephony:bridge token: ' token
echo
[[ $token =~ ^[A-Za-z0-9_.-]{16,512}$ ]]
origin=${1:-https://notify.alxprgs.tech}
[[ $origin =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]]
destination=/opt/alxprgs-notify/asterisk
install -d -m 755 "$destination"
install -m 644 bridge.py requirements.txt "$destination/"
python3 -m venv "$destination/venv"
"$destination/venv/bin/python" -m pip install -r "$destination/requirements.txt"
install -m 644 notify-asterisk.service /etc/systemd/system/notify-asterisk.service
password=$(openssl rand -hex 24)
umask 077
install -d -m 700 /etc/alxprgs-notify
printf 'NOTIFY_URL=%s\nNOTIFY_BRIDGE_TOKEN=%s\nARI_PORT=8088\nARI_USER=notify\nARI_PASSWORD=%s\nASTERISK_ENDPOINT=PJSIP/%s\n' \
    "$origin" "$token" "$password" "$endpoint" > /etc/alxprgs-notify/asterisk.env
printf '[general]\nenabled=yes\nbindaddr=127.0.0.1\nbindport=8088\n' > /etc/asterisk/http.conf
printf '[general]\nenabled=yes\npretty=no\n[notify]\ntype=user\nread_only=no\npassword=%s\n' "$password" > /etc/asterisk/ari.conf
chgrp asterisk /etc/asterisk/{http,ari}.conf
chmod 640 /etc/asterisk/{http,ari}.conf
systemctl restart asterisk
ready=false
for attempt in {1..20}; do
    if asterisk -rx 'http show status' 2>/dev/null | grep -q '127.0.0.1:8088'; then
        ready=true
        break
    fi
    sleep 1
done
if [[ $ready != true ]]; then
    echo "ARI listener is unavailable. Inspect systemctl status asterisk."
    exit 1
fi
systemctl daemon-reload
systemctl enable --now notify-asterisk
systemctl is-active notify-asterisk
