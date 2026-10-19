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


class ViewportTests(unittest.TestCase):
    @staticmethod
    def tree(width=1080, height=1920, texts=()):
        root = ET.Element('hierarchy')
        container = ET.SubElement(root, 'node', dict(package='ru.lenscast', bounds=f'[0,24][{width},{height}]'))
        for text, bounds in texts:
            ET.SubElement(container, 'node', dict(text=text, bounds=bounds, enabled='true'))
        return root

    def test_swipes_stay_within_portrait_landscape_and_small_windows(self):
        for width, height in [(1080, 1920), (1920, 1080), (480, 800)]:
            for direction in ('up', 'down'):
                with self.subTest(size=(width, height), direction=direction), patch.object(smoke, 'adb') as adb:
                    smoke.scroll_ui(self.tree(width, height), direction)
                    args = adb.call_args.args
                    x, y, xx, yy = map(int, args[3:7])
                    self.assertTrue(0 < x == xx < width)
                    self.assertTrue(24 < y < height and 24 < yy < height)
                    self.assertEqual(y > yy, direction == 'down')

    def test_offscreen_positive_node_is_not_tapped(self):
        with patch.object(smoke, 'adb') as adb:
            self.assertFalse(smoke.tap_node(ET.Element('node', dict(bounds='[10,1500][110,1600]')), (0, 24, 1920, 1080)))
            adb.assert_not_called()

    def test_start_above_camera_panel_is_found_by_upward_search(self):
        below = self.tree(texts=[('Запустить камеру', '[20,400][400,460]')])
        above = self.tree(texts=[('Старт', '[20,600][400,660]')])
        with patch.object(smoke, 'ui', side_effect=[below, above]), patch.object(smoke, 'adb') as adb:
            smoke.find_and_tap(lambda n: n.get('text') == 'Старт', 'Старт')
            self.assertEqual(adb.call_count, 2)
            self.assertEqual(adb.call_args_list[0].args[2], 'swipe')
            self.assertEqual(adb.call_args_list[1].args, ('shell', 'input', 'tap', '210', '630'))

    def test_target_below_heading_is_found_by_downward_search(self):
        top = self.tree(texts=[('Выберите режим', '[20,100][400,160]')])
        target = self.tree(texts=[('Старт', '[20,600][400,660]')])
        with patch.object(smoke, 'ui', side_effect=[top, target]), patch.object(smoke, 'adb') as adb:
            smoke.find_and_tap(lambda n: n.get('text') == 'Старт', 'Старт')
            args = adb.call_args_list[0].args
            self.assertGreater(int(args[4]), int(args[6]))
            self.assertEqual(adb.call_args_list[1].args[2], 'tap')


if __name__ == '__main__':
    unittest.main()
