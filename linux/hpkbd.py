#!/usr/bin/env python3
"""Userspace control of the HP ZBook Studio G8 keyboard backlight via hidraw.

No kernel module needed, but no hotkeys either: the backlight key never reaches
userspace (see the module for that). Use this OR the hid-hp-zbook-kbd module,
not both at once. Needs write access to the hidraw node (root, or install
99-hp-zbook-kbd.rules).

  hpkbd.py info
  hpkbd.py on | off
  hpkbd.py color RRGGBB [-b 0-100]
  hpkbd.py effect NAME [RRGGBB ...]     NAME: wave breathing colorloop sparkle raindrop
                                              ripple ghosting omenx confetti sun swipe
"""
import argparse
import fcntl
import glob
import re
import os
import select
import sys
import time
from pathlib import Path

VID, PID, IFACE = 0x1FC9, 0x2238, 2
KEYS, PAGE = 144, 60

# McuSDK2 effect id, preset (0 = custom colour), default colour, speed  (OMEN defaults)
EFFECTS = {
    "wave": (10, 5, None, 1), "breathing": (2, 0, 0xEA002A, 0), "colorloop": (4, 2, None, 1),
    "sparkle": (7, 4, None, 1), "raindrop": (12, 0, 0x0FFA36, 2), "ripple": (9, 4, None, 1),
    "ghosting": (8, 3, None, 1), "omenx": (13, 2, None, 1), "confetti": (15, 5, None, 1),
    "sun": (16, 5, None, 1), "swipe": (17, 5, None, 1),
}


def board_allowed(allow_unsupported=False, dmi=Path('/sys/class/dmi/id')):
    vendor = (dmi / 'sys_vendor').read_text().strip()
    board = (dmi / 'board_name').read_text().strip()
    return vendor in ('HP', 'Hewlett-Packard') and (board == '8873' or allow_unsupported)


def find_node():
    for dev in glob.glob("/sys/class/hidraw/hidraw*"):
        try:
            uevent = open(os.path.join(dev, "device/uevent")).read()
        except OSError:
            continue
        if f"HID_ID=0003:{VID:08X}:{PID:08X}" not in uevent:
            continue
        # .../1-7:1.2/0003:1FC9:2238.0005 -> USB interface number after the last '.'
        usb_if = os.path.basename(os.path.dirname(os.path.realpath(os.path.join(dev, "device"))))
        if usb_if.rsplit(".", 1)[-1] == str(IFACE):
            return "/dev/" + os.path.basename(dev)
    sys.exit("keyboard MCU (1fc9:2238 interface 2) not found")


class Kbd:
    def __init__(self, verbose=False, allow_unsupported=False):
        if not board_allowed(allow_unsupported):
            raise RuntimeError('unverified board; use hpkbd-diagnose first, then --allow-unsupported only for the same protocol')
        node = find_node()
        device = os.path.realpath('/sys/class/hidraw/' + os.path.basename(node) + '/device')
        if os.path.basename(os.path.realpath(device + '/driver')) == 'hp-zbook-kbd':
            raise RuntimeError('kernel driver is active; use hpkbd instead')
        self.fd = os.open(node, os.O_RDWR | os.O_CLOEXEC)
        try:
            fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            os.close(self.fd)
            raise
        self.verbose = verbose

    def xfer(self, cmd, idx, payload=b"", length=None, check=True):
        if len(payload) > 60:
            raise ValueError('payload exceeds 60 bytes')
        length = len(payload) if length is None else length
        pkt = (bytes([cmd, idx, length & 0xFF, length >> 8]) + bytes(payload)).ljust(64, b"\0")
        while select.select([self.fd], [], [], 0)[0]:   # drain stale reports
            os.read(self.fd, 64)
        if self.verbose:
            print(">>", pkt[: 4 + max(len(payload), 4)].hex(" "), file=sys.stderr)
        written = os.write(self.fd, b"\0" + pkt)                   # report id 0
        if written != 65:
            raise IOError('short HID write')
        deadline = time.monotonic() + 1.0
        while True:
            left = deadline - time.monotonic()
            if left <= 0 or not select.select([self.fd], [], [], left)[0]:
                raise TimeoutError(f"no reply to 0x{cmd:02X}")
            r = os.read(self.fd, 64)
            if len(r) == 64 and r[0] == cmd and r[1] == idx and (not check or (r[4] == 0xEC and r[5] in (0xAC, 0xFA))):
                break
        if self.verbose:
            print("<<", r[:12].hex(" "), file=sys.stderr)
        if check and (r[4] != 0xEC or r[5] != 0xAC):
            raise IOError(f"MCU rejected 0x{cmd:02X}: {r[:8].hex(' ')}")
        return r

    def power(self, on):
        self.xfer(0x09, 0, b"\x01" if on else b"\x00")   # START / OFF

    def static(self, r, g, b):
        self.power(True)
        for c, plane in enumerate((r, g, b)):
            for page in range(3):
                self.xfer(0x05 + c, page, plane[page * PAGE:(page + 1) * PAGE], length=0)

    def effect(self, name, colors):
        if len(colors) > 6 or any(type(c) is not int or not 0 <= c <= 0xffffff for c in colors):
            raise ValueError('expected up to six RGB colours')
        eid, preset, default, speed = EFFECTS[name]
        if not colors and not preset:
            colors = [default]
        d = bytearray(60)
        d[0] = eid
        if colors:
            d[1], d[2] = (1 if len(colors) > 1 else 0), len(colors) - 1
        else:
            d[1], d[2] = preset, 4
        d[3], d[4], d[5], d[6], d[7] = speed, 3, 3, 1, speed
        for i, c in enumerate(colors[:6]):
            d[24 + 3 * i: 27 + 3 * i] = c.to_bytes(3, "big")
        self.power(True)
        self.xfer(0x03, 0, bytes(d), length=0x24)


def rgb(value):
    value = value.removeprefix('#')
    if not re.fullmatch(r'[0-9a-fA-F]{6}', value):
        raise argparse.ArgumentTypeError('colour must be six hex digits')
    return int(value, 16)


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('-v', action='store_true')
    parser.add_argument('--allow-unsupported', action='store_true', help='opt in another HP board with the same controller/protocol')
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('info', 'on', 'off'):
        commands.add_parser(name)
    color = commands.add_parser('color')
    color.add_argument('rgb', type=rgb)
    color.add_argument('-b', type=int, choices=range(101), default=100, metavar='0-100')
    effect = commands.add_parser('effect')
    effect.add_argument('name', choices=EFFECTS)
    effect.add_argument('colors', nargs='*', type=rgb)
    args = parser.parse_args(argv)
    if args.command == 'effect' and len(args.colors) > 6:
        parser.error('at most six colours are allowed')
    try:
        k = Kbd(args.v, args.allow_unsupported)
        try:
            if args.command == 'info':
                print('raw:', k.xfer(0x80, 1, check=False)[:16].hex(' '))
            elif args.command in ('on', 'off'):
                k.power(args.command == 'on')
            elif args.command == 'color':
                values = [((args.rgb >> shift) & 255) * args.b // 100 for shift in (16, 8, 0)]
                k.static(*(bytes([c]) * KEYS for c in values))
            else:
                k.effect(args.name, args.colors)
        finally:
            os.close(k.fd)
    except (OSError, RuntimeError) as error:
        parser.exit(1, f'hpkbd-raw: {error}\n')


if __name__ == '__main__':
    main(sys.argv[1:])
