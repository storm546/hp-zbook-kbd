#!/usr/bin/env bash
set -euo pipefail

version=0.4
module=hid-hp-zbook-kbd
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
target_user=${SUDO_USER:-}
if [[ ${1:-} == --user && $# == 2 ]]; then
  target_user=$2
elif [[ $# != 0 ]]; then
  echo "Usage: sudo ./install.sh [--user USER]" >&2
  exit 2
fi
if (( EUID != 0 )); then
  echo "Run with sudo." >&2
  exit 1
fi
if [[ -z $target_user || $target_user == root ]]; then
  echo "Choose the desktop account: sudo ./install.sh --user USER" >&2
  exit 1
fi
for command in dkms make gcc python3 udevadm getent; do
  command -v "$command" >/dev/null || { echo "Missing dependency: $command" >&2; exit 1; }
done
target_group=$(id -gn -- "$target_user")
[[ $target_group =~ ^[a-zA-Z0-9_.-]+$ ]] || { echo "Unsupported group name" >&2; exit 1; }
[[ -d /lib/modules/$(uname -r)/build ]] || { echo "Install headers for the running kernel first." >&2; exit 1; }

install -d "/usr/src/$module-$version"
install -m 0644 "$source_dir/Makefile" "$source_dir/dkms.conf" "$source_dir/hid-hp-zbook-kbd.c" "/usr/src/$module-$version/"
# Build first, so a compiler failure leaves the working installation intact.
if ! dkms status -m "$module" -v "$version" | grep -q .; then
  dkms add "$module/$version"
fi
for headers in /lib/modules/*/build; do
  [[ -d $headers ]] || continue
  kernel=$(basename "$(dirname "$headers")")
  dkms build "$module/$version" -k "$kernel"
done
# Older installed versions would compete for the same USB device.
old_versions=$(dkms status -m "$module" | sed -n "s|^$module/\([^,:]*\).*|\1|p" | sort -u)
for old in $old_versions; do
  [[ $old == "$version" ]] || dkms remove "$module/$old" --all
done
for headers in /lib/modules/*/build; do
  [[ -d $headers ]] || continue
  kernel=$(basename "$(dirname "$headers")")
  dkms install "$module/$version" -k "$kernel"
done
install -m 0755 "$source_dir/hpkbd" /usr/local/bin/hpkbd
install -m 0755 "$source_dir/hpkbd-diagnose" /usr/local/bin/hpkbd-diagnose
install -D -m 0644 "$source_dir/hpkbd-restore.service" /etc/systemd/user/hpkbd-restore.service
rules=$(mktemp)
trap 'rm -f "$rules"' EXIT
sed "s/@GROUP@/$target_group/g" "$source_dir/99-hp-zbook-kbd.rules" > "$rules"
install -m 0644 "$rules" /etc/udev/rules.d/99-hp-zbook-kbd.rules
printf '%s\n' "$module" > /etc/modules-load.d/hp-zbook-kbd.conf
udevadm control --reload-rules
if [[ -d /sys/module/hid_hp_zbook_kbd ]]; then
  modprobe -r hid_hp_zbook_kbd
fi
modprobe "$module"
udevadm settle
if [[ ! -e /sys/class/leds/hp::kbd_backlight/effect ]]; then
  echo "Installed, but no supported keyboard bound. Run hpkbd-diagnose; see COMPATIBILITY.md." >&2
  exit 1
fi
printf '%s\n' 'Installed. As your desktop user, run:' '  systemctl --user daemon-reload' '  systemctl --user enable --now hpkbd-restore.service'
