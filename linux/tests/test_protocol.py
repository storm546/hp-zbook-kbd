"""Regression tests for full-sized effect packets without opening hardware."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('hpkbd_raw', Path(__file__).parents[1] / 'hpkbd.py')
raw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(raw)


class ProtocolTests(unittest.TestCase):
    def keyboard(self):
        k = raw.Kbd.__new__(raw.Kbd)
        k.fd = 123
        k.verbose = False
        return k

    def test_six_colors_preserve_windows_header_and_all_bytes(self):
        k = self.keyboard()
        colors = [0xff0000, 0x00ff00, 0x0000ff, 0xffff00, 0xff00ff, 0x00ffff]
        with patch.object(k, 'power') as power, patch.object(k, 'xfer') as xfer:
            k.effect('breathing', colors)
        power.assert_called_once_with(True)
        args, kwargs = xfer.call_args
        self.assertEqual(args[:2], (3, 0))
        self.assertEqual(len(args[2]), 60)
        self.assertEqual(args[2][24:42], b''.join(c.to_bytes(3, 'big') for c in colors))
        self.assertEqual(kwargs['length'], 0x24)
        self.assertEqual(args[2][2], 5)

    def test_oversize_payload_is_rejected_before_io(self):
        with self.assertRaises(ValueError):
            self.keyboard().xfer(3, 0, bytes(61))

    def test_short_and_unrelated_replies_are_ignored(self):
        k = self.keyboard()
        good = bytes([9, 0, 0, 0, 0xec, 0xac]) + bytes(58)
        replies = [b'\x09', bytes([9, 1]) + bytes(62), bytes([9, 0, 0, 0, 0xec, 0xbd]) + bytes(58), good]
        with patch.object(raw.select, 'select', side_effect=[([], [], [])] + [([123], [], [])] * 4), patch.object(raw.os, 'write', return_value=65), patch.object(raw.os, 'read', side_effect=replies):
            self.assertEqual(k.xfer(9, 0, b'\x01'), good)

    def test_invalid_colors(self):
        for value in ('', '12345', '1234567', 'xyzxyz', '-12345'):
            with self.subTest(value=value), self.assertRaises(raw.argparse.ArgumentTypeError):
                raw.rgb(value)


if __name__ == '__main__':
    unittest.main()
