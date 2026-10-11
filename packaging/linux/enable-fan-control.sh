#!/usr/bin/env bash
# Optional: lets members of the 'fanzi' group set fan duty (hwmon pwm files) without running FANZi as root.
# Usage: sudo ./enable-fan-control.sh [user]   — then log out and back in.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo." >&2; exit 1; }
USER_NAME="${1:-${SUDO_USER:-}}"
groupadd -f fanzi
[ -n "$USER_NAME" ] && usermod -aG fanzi "$USER_NAME"
cat > /etc/udev/rules.d/90-fanzi-io-nity.rules <<'RULE'
# FANZi IO-nity: group 'fanzi' may write fan PWM duty / mode on every hwmon chip.
ACTION=="add", SUBSYSTEM=="hwmon", RUN+="/bin/sh -c 'chgrp fanzi /sys%p/pwm[0-9]* 2>/dev/null; chmod g+w /sys%p/pwm[0-9]* 2>/dev/null; true'"
RULE
udevadm control --reload-rules
udevadm trigger --subsystem-match=hwmon --action=add
echo "Fan control enabled for group 'fanzi'${USER_NAME:+ (added $USER_NAME)}. Log out and back in, then restart FANZi."
