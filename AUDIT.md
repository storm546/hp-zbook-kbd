# Audit notes

The Linux code was reviewed and tested on a ZBook Studio G8 in November 2026.
Fixes include:

- A kernel stack overflow with five or six effect colours.
- Missing sysfs controls and incorrect handling of other USB interfaces.
- Loose colour parsing, incomplete reply checks and world-writable controls.
- Hotkeys affecting external keyboards and missing normal-resume handling.

Version 0.3 was tested on hardware with Linux 7.2.11. Its module also built for
7.2.7-cachyos and 7.2.8-omarchy. The backlight key and effect shortcuts worked.
Fourteen invalid sysfs requests were rejected. Protocol regression tests cover
six-colour packets and short/unrelated replies.

Version 0.4.1 adds report-layout checks and opt-in support for untested HP boards.
It also avoids an ACK timeout when the LED core unregisters during driver unload.
A full reboot and suspend/resume test are still needed. Windows was checked for
packet compatibility; it has not had the same audit.

Run tests with `python3 -m unittest discover -s linux/tests -v`.
