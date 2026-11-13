# hp-zbook-kbd

RGB keyboard control for the HP ZBook Studio G8, without OMEN Gaming Hub.
Windows and Linux. Static colours, per-key colours and built-in effects.

Tested on the **Studio G8, board 8873**, with USB controller **1fc9:2238**.
Other models are experimental; see [COMPATIBILITY.md](COMPATIBILITY.md).

## Linux

Needs Python 3, DKMS, make, a C compiler and headers for your kernel.
Tested on Linux 7.2. Older kernels may need changes.

```sh
git clone https://github.com/storm546/hp-zbook-kbd.git
cd hp-zbook-kbd
sudo ./linux/install.sh
systemctl --user daemon-reload
systemctl --user enable --now hpkbd-restore.service
```

The installer gives your primary group access to the lighting controls and builds
for installed kernels with headers. It needs systemd and udev.

```sh
hpkbd color 00A0FF -b 50
hpkbd effect wave
hpkbd effect breathing FF0000
hpkbd brightness 50
hpkbd off
hpkbd status
```

CLI settings restore at login. Hotkey changes last until the next login.
Effects support on/off; their intermediate brightness is ignored by the firmware.

## Windows

Run `windows\build.cmd`, then use `windows\hpkbd.exe`.
The compiler ships with .NET Framework; no extra SDK is needed.

```text
hpkbd install
hpkbd color 00A0FF -b 50
hpkbd effect wave
hpkbd effect wave -p ocean -s fast -d rtl
hpkbd off
hpkbd uninstall
```

`install` starts the hotkey daemon at login. It also restores lighting after sleep
and unlock. Settings are in `%APPDATA%\hpkbd\state.txt`.
OMEN Gaming Hub may overwrite lighting while running.

## Hotkeys

On the Studio G8:

- Backlight key: full → half → off for static colours; on/off for effects.
- Right Alt + 0: static colour.
- Right Alt + 1–9: wave, breathing, colorloop, sparkle, raindrop, ripple,
  ghosting, omenx, confetti.

Linux hotkeys are disabled on untested boards. To disable them on the G8, put
`options hid_hp_zbook_kbd hotkeys=0` in `/etc/modprobe.d/hp-zbook-kbd.conf`.

[Protocol](PROTOCOL.md) · [Audit notes](AUDIT.md) · [Removal](linux/UNINSTALL.md)

GPL-2.0-or-later. Not affiliated with HP.
