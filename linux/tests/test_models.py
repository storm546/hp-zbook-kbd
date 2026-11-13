"""Model checks use temporary sysfs data; no device is opened."""
import importlib.machinery
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from test_protocol import raw

path = Path(__file__).parents[1] / 'hpkbd-diagnose'
loader = importlib.machinery.SourceFileLoader('diagnose', str(path))
spec = importlib.util.spec_from_loader(loader.name, loader)
diagnose = importlib.util.module_from_spec(spec)
loader.exec_module(diagnose)


class ModelTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dmi = Path(self.tmp.name)

    def allowed(self, vendor, board, experimental=False):
        (self.dmi / 'sys_vendor').write_text(vendor + '\n')
        (self.dmi / 'board_name').write_text(board + '\n')
        return raw.board_allowed(experimental, self.dmi)

    def test_known_board(self):
        self.assertTrue(self.allowed('HP', '8873'))
        self.assertTrue(self.allowed('Hewlett-Packard', '8873'))

    def test_unknown_board_requires_opt_in(self):
        self.assertFalse(self.allowed('HP', '9999'))
        self.assertTrue(self.allowed('HP', '9999', True))

    def test_other_vendors_never_allowed(self):
        self.assertFalse(self.allowed('Other', '8873'))
        self.assertFalse(self.allowed('Other', '9999', True))

    def test_rejected_board_opens_no_device(self):
        with patch.object(raw, 'board_allowed', return_value=False), patch.object(raw, 'find_node') as find:
            with self.assertRaises(RuntimeError):
                raw.Kbd()
            find.assert_not_called()

    def test_missing_sysfs_report(self):
        result = diagnose.report(self.dmi)
        self.assertFalse(result['tested_model'])
        self.assertEqual(result['controllers'], [])
        self.assertNotIn('serial', result)

    def test_diagnostic_report_excludes_serial(self):
        dmi = self.dmi / 'class/dmi/id'
        dmi.mkdir(parents=True)
        for name, value in {'sys_vendor': 'HP', 'product_name': 'Studio', 'board_name': '8873', 'product_serial': 'PRIVATE'}.items():
            (dmi / name).write_text(value)
        result = diagnose.report(self.dmi)
        self.assertTrue(result['tested_model'])
        self.assertNotIn('PRIVATE', str(result))
