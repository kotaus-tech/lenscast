"""Host-only tests of UI coordinate safety; actual input remains tested on AVD."""
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import android_link_smoke as smoke


class TapNodeTests(unittest.TestCase):
    def test_offscreen_empty_disabled_and_malformed_nodes_are_not_tapped(self):
        for attrs in [dict(bounds='[0,-900][400,-800]'), dict(bounds='[-30,20][100,50]'),
                      dict(bounds='[0,0][0,0]'), dict(bounds=''),
                      dict(bounds='[10,20][100,80]', enabled='false')]:
            with self.subTest(attrs=attrs), patch.object(smoke, 'adb') as adb:
                self.assertFalse(smoke.tap_node(ET.Element('node', attrs)))
                adb.assert_not_called()

    def test_visible_enabled_node_is_tapped_at_its_center(self):
        with patch.object(smoke, 'adb') as adb:
            self.assertTrue(smoke.tap_node(ET.Element('node', dict(bounds='[10,20][110,80]', enabled='true'))))
            adb.assert_called_once_with('shell', 'input', 'tap', '60', '50')


if __name__ == '__main__':
    unittest.main()
