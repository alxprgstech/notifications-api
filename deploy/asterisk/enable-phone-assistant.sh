#!/usr/bin/env bash
# Install the inbound route on an existing Notify/Asterisk host.
set -euo pipefail
export LC_ALL=C
[[ $EUID == 0 ]] || { echo "Run with sudo."; exit 1; }
[[ $# == 4 ]] || { echo "Usage: $0 EXTENSION CONTEXT ENDPOINT PHONE_IPV4"; exit 1; }
number=$1 context=$2 endpoint=$3 ip=$4
[[ $number =~ ^[0-9]{2,8}$ && $context =~ ^[A-Za-z0-9_.-]+$ &&
   $endpoint =~ ^[A-Za-z0-9_.-]+$ ]] || exit 1
python3 -c 'import ipaddress,sys; assert ipaddress.IPv4Address(sys.argv[1]).is_private' "$ip"
source_bridge=$(dirname "$(realpath "$0")")/bridge.py
config=/etc/asterisk/extensions.conf
environment=/etc/alxprgs-notify/asterisk.env
bridge=/opt/alxprgs-notify/asterisk/bridge.py
systemctl is-active --quiet asterisk
systemctl is-active --quiet notify-asterisk
asterisk -rx 'core show channels count' | grep -qx '0 active calls'
endpoint_info=$(asterisk -rx "pjsip show endpoint $endpoint")
grep -Eq '^[[:blank:]]*InAuth:[[:blank:]]+[^<[:blank:]]' <<<"$endpoint_info"
awk -v expected="$context" '$1 == "context" && $2 == ":" && $3 == expected {found=1} END {exit !found}' <<<"$endpoint_info"
dialplan=$(asterisk -rx "dialplan show $number@$context" || true)
grep -Fq "There is no existence of $number@$context extension" <<<"$dialplan"
channel_help=$(asterisk -rx 'core show function CHANNEL')
grep -q 'remote_addr' <<<"$channel_help"
grep -Fxq "ASTERISK_ENDPOINT=PJSIP/$endpoint" "$environment"
if grep -Eq '^ASTERISK_ASSISTANT_(EXTENSION|CONTEXT)=' "$environment"; then
    echo "An assistant route is already configured."
    exit 1
fi
python3 -c 'import ast,sys; ast.parse(open(sys.argv[1], encoding="utf-8").read())' "$source_bridge"
umask 077
backup=$(mktemp -d /var/backups/notify-phone-assistant.XXXXXXXX)
cp -a "$config" "$environment" "$bridge" "$backup/"
rollback() {
    trap - ERR
    cp -a "$backup/extensions.conf" "$config"
    cp -a "$backup/asterisk.env" "$environment"
    cp -a "$backup/bridge.py" "$bridge"
    asterisk -rx 'dialplan reload'
    systemctl restart notify-asterisk
    echo "Restored files from $backup" >&2
}
trap rollback ERR
python3 - "$config" "$number" "$context" "$ip" <<'PY'
import pathlib
import sys

path, number, context, phone = sys.argv[1:]
path = pathlib.Path(path)
text = path.read_text(encoding="utf-8")
header = f"[{context}]\n"
if text.count(header) != 1:
    raise SystemExit("Expected exactly one local context section")
route = (
    f'exten => {number},1,Set(NOTIFY_SIP_REMOTE=${{CHANNEL(pjsip,remote_addr)}})\n'
    f' same => n,GotoIf($["${{CUT(NOTIFY_SIP_REMOTE,:,1)}}" = "{phone}"]?trusted)\n'
    ' same => n,Hangup(21)\n'
    f' same => n(trusted),Stasis(alxprgs-notify,phone-assistant,{number})\n'
    ' same => n,Hangup()\n'
)
path.write_text(text.replace(header, header + route, 1), encoding="utf-8")
PY
printf '\nASTERISK_ASSISTANT_EXTENSION=%s\nASTERISK_ASSISTANT_CONTEXT=%s\n' "$number" "$context" >> "$environment"
install -o root -g root -m 644 "$source_bridge" "$bridge"
asterisk -rx 'dialplan reload'
asterisk -rx "dialplan show $number@$context" | grep -F "Stasis(alxprgs-notify,phone-assistant,$number)"
systemctl restart notify-asterisk
systemctl is-active --quiet notify-asterisk
trap - ERR
echo "Backup: $backup"
