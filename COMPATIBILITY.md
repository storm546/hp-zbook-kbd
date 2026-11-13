# Models

| Model | Status |
| --- | --- |
| HP ZBook Studio G8, board 8873 | Tested on Windows and Linux |
| Other HP models with 1fc9:2238 | Experimental, opt-in on Linux |
| Different controllers or single-colour keyboards | Unsupported |

The USB ID alone does not prove compatibility. This driver expects interface 2,
unnumbered 64-byte input/output reports, HP's McuSDK2 lighting commands and a
144-key colour map. Other HP/OMEN keyboards can use different protocols.

## Check another model

Start with a hardware report; this sends no commands:

```sh
python3 linux/hpkbd-diagnose
```

Open an issue with the output. Model reports help us add tested profiles.

If your HP laptop has the same controller **and you have confirmed the protocol**,
you can test the module explicitly:

```sh
make -C linux
sudo modprobe led-class-multicolor
sudo insmod linux/hid-hp-zbook-kbd.ko allow_unsupported=1 hotkeys=0
```

Do this before installing. If the module is already loaded, unload it first with
`sudo modprobe -r hid_hp_zbook_kbd`. The driver checks the report sizes before
sending its device-info query. A successful query alone does not prove lighting
compatibility. Only lighting commands are used; no firmware updates or flash writes.

After a successful test, put this in `/etc/modprobe.d/hp-zbook-kbd.conf` and install:

```text
options hid_hp_zbook_kbd allow_unsupported=1 hotkeys=0
```

Hotkeys stay disabled on untested boards regardless of `hotkeys=1`; their scan
codes need separate verification. The Windows tool selects the same USB ID and
interface, but has no model check. It has only been tested on the Studio G8.

To add a model to the tested list, include its board ID, hardware report, lighting
test results and resume behaviour. A different controller needs its own protocol
implementation, not a wider USB match.
