# Remove Linux setup

Run these as your desktop user:

```sh
systemctl --user disable --now hpkbd-restore.service
sudo rm -f /etc/modules-load.d/hp-zbook-kbd.conf /etc/udev/rules.d/99-hp-zbook-kbd.rules
sudo modprobe -r hid_hp_zbook_kbd
sudo dkms remove hid-hp-zbook-kbd/0.4.1 --all
sudo rm -f /usr/local/bin/hpkbd /usr/local/bin/hpkbd-diagnose
sudo rm -f /etc/systemd/user/hpkbd-restore.service
rm -f ~/.config/systemd/user/hpkbd-restore.service
systemctl --user daemon-reload
sudo udevadm control --reload-rules
```

If you added `/etc/modprobe.d/hp-zbook-kbd.conf`, remove that too.
Saved settings in `~/.local/state/hpkbd/` are left in place.
