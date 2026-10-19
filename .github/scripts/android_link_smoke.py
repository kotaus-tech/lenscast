"""Exercise the signed APK, real FGS/KeyStore/TLS and both sockets on an emulator.

This is NOT a Windows USB driver or physical Wi-Fi discovery test.
Do not print UI XML, pairing codes, tokens or control JSON.
"""
import hashlib
import json
import re
import socket
import ssl
import struct
import subprocess
import sys
import time
import threading
import xml.etree.ElementTree as ET


def adb(*args, timeout=30):
    return subprocess.check_output(['adb', *args], timeout=timeout).decode('utf-8', 'replace')


def ui():
    adb('shell', 'uiautomator', 'dump', '/sdcard/lenscast-ui.xml')
    return ET.fromstring(adb('shell', 'cat', '/sdcard/lenscast-ui.xml'))


def send(stream, message):
    data = json.dumps(message, ensure_ascii=False).encode('utf-8')
    stream.sendall(struct.pack('!I', len(data)) + data)


def exactly(stream, count):
    data = b''
    while len(data) < count:
        part = stream.recv(count-len(data))
        if not part:
            raise AssertionError('Соединение неожиданно закрыто')
        data += part
    return data


def read(stream):
    length, = struct.unpack('!I', exactly(stream, 4))
    assert 2 <= length <= 65536, 'Недопустимый размер ответа'
    return json.loads(exactly(stream, length))


def wait_type(stream, kind):
    for _ in range(12):
        message = read(stream)
        if message.get('type') == kind:
            return message
    raise AssertionError('Ожидаемый ответ не получен')


def start(apk, mode="Экран"):
    # Cold AVD package installation can exceed 30 seconds under hosted-runner load.
    # This is not a transport timeout; all other adb operations keep their limit.
    adb('install', '-r', apk, timeout=120)
    sdk = int(adb('shell', 'getprop', 'ro.build.version.sdk').strip())
    if sdk >= 33:
        adb('shell', 'pm', 'grant', 'ru.lenscast', 'android.permission.POST_NOTIFICATIONS')
    adb('shell', 'am', 'start', '-W', '-n', 'ru.lenscast/.MainActivity')
    if mode == 'Веб-камера':
        for _ in range(10):
            card = next((n for n in ui().iter('node') if n.get('text', '').endswith('Веб-камера')), None)
            if card is not None and tap_node(card): break
            adb('shell', 'input', 'swipe', '500', '500', '500', '1550', '300')
        else: raise AssertionError('Не найдена карточка выбора веб-камеры')
    for _ in range(6):
        root = ui()
        start = next((n for n in root.iter('node') if n.get('text') == 'Старт'), None)
        if start is not None:
            x1, y1, x2, y2 = map(int, re.findall(r'\d+', start.get('bounds', '')))
            if x2 > x1 and y2 > y1:
                adb('shell', 'input', 'tap', str((x1+x2)//2), str((y1+y2)//2))
                break
        adb('shell', 'input', 'swipe', '500', '1550', '500', '500', '300')
    else:
        raise AssertionError('Кнопка «Старт» недоступна')

    code = None
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        root = ui()
        code = next((n.get('text') for n in root.iter('node') if re.fullmatch(r'\d{6}', n.get('text', ''))), None)
        if code:
            break
        time.sleep(1)
    assert code, 'Служба не подготовила Wi-Fi: проверьте журнал AndroidRuntime'
    adb('forward', 'tcp:27185', 'tcp:27185')
    adb('forward', 'tcp:27183', 'tcp:27183')
    return sdk, code


def tls_context(tls12=False):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # First pairing is confirmed using the on-screen code.
    if tls12:
        context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_2
    return context


def exercise(apk, remembered=None, tls12=False):
    sdk, code = start(apk)
    with socket.create_connection(('127.0.0.1', 27185), timeout=8) as usb:
        usb.settimeout(8)
        send(usb, dict(type='hello', version=1, code='', token=''))
        assert read(usb)['type'] == 'ready', 'USB-сервер не принял доверенное соединение'
        assert wait_type(usb, 'telemetry')['battery'] >= 0, 'Нет телеметрии'
        send(usb, dict(type='ping', sent=123))
        assert wait_type(usb, 'pong')['sent'] == 123, 'Команды USB не принимаются'
    print(f'Пройдено: Android {sdk}, foreground service, loopback USB, телеметрия и команды')

    context = tls_context(tls12)
    time.sleep(1)
    if remembered is None:
        for wrong in [dict(code='000000' if code != '000000' else '000001', token=''),
                      dict(code='', token='f' * 64)]:
            with socket.create_connection(('127.0.0.1', 27183), timeout=8) as tcp:
                with context.wrap_socket(tcp, server_hostname='Lenscast') as tls:
                    send(tls, dict(type='hello', version=1, **wrong))
                    assert read(tls).get('type') == 'pairing', 'TLS позволил обойти сопряжение'
        print('Пройдено: неверные код и токен не дают доступа после TLS-рукопожатия')
    with socket.create_connection(('127.0.0.1', 27183), timeout=8) as tcp:
        with context.wrap_socket(tcp, server_hostname='Lenscast') as tls:
            negotiated = tls.version()
            fingerprint = hashlib.sha256(tls.getpeercert(binary_form=True)).digest()
            if remembered:
                assert fingerprint == remembered[0], 'Обновление совместимого ключа изменило сертификат'
            send(tls, dict(type='hello', version=1, code='' if remembered else code, token=remembered[1] if remembered else ''))
            ready = read(tls)
            assert ready.get('type') == 'ready', 'Первое TLS-сопряжение не выполнено'
            token = remembered[1] if remembered else ready.get('token', '')
            assert len(token) == 64, 'Телефон не выдал токен'
            send(tls, dict(type='ping', sent=456))
            assert wait_type(tls, 'pong')['sent'] == 456, 'TLS-команды не принимаются'
    print('Пройдено: AndroidKeyStore, ' + negotiated + ' и доверенное подключение')
    time.sleep(1)
    with socket.create_connection(('127.0.0.1', 27183), timeout=8) as tcp:
        with context.wrap_socket(tcp, server_hostname='Lenscast') as tls:
            assert hashlib.sha256(tls.getpeercert(binary_form=True)).digest() == fingerprint, 'Сертификат изменился'
            send(tls, dict(type='hello', version=1, code='', token=token))
            assert read(tls).get('type') == 'ready', 'Токен не принят после переподключения'
            send(tls, dict(type='stop'))
    print('Пройдено: повторное TLS-сопряжение без кода и команда остановки')
    return fingerprint, token


def tap_node(node):
    x1, y1, x2, y2 = map(int, re.findall(r'\d+', node.get('bounds', '')))
    if x2 <= x1 or y2 <= y1:
        return False
    adb('shell', 'input', 'tap', str((x1+x2)//2), str((y1+y2)//2))
    return True


def video_packet(stream):
    magic, version, kind, codec, flags, stamp, size, sequence = struct.unpack('!IBBBBQII', exactly(stream, 24))
    assert magic == 0x4c435631 and version == 1 and kind in (0, 1, 2) and codec in (1, 2) and flags == 0, 'Неверный видеозаголовок'
    assert 1 <= size <= 16777216, 'Неверный размер видео'
    return kind, exactly(stream, size)


def attach_video(ticket):
    stream = socket.create_connection(('127.0.0.1', 27186), timeout=15)
    send(stream, dict(version=1, ticket=ticket))
    return stream


def wait_key(stream, landscape=False):
    configured = False
    deadline = time.monotonic() + 25
    for _ in range(250):
        assert time.monotonic() < deadline, "Ключевой кадр не получен вовремя"
        kind, body = video_packet(stream)
        if kind == 0:
            width, height = struct.unpack('!II', body[:8])
            assert 16 <= width <= 4096 and 16 <= height <= 4096 and width % 2 == height % 2 == 0, 'Неверные размеры экрана'
            assert body[8:11] == b'\x00\x00\x01' or body[8:12] == b'\x00\x00\x00\x01', 'Нет Annex B конфигурации'
            configured = width > height if landscape else True
        elif kind == 2 and configured:
            return
    raise AssertionError('Нет конфигурации и ключевого кадра')


class ControlSession:
    """Like the desktop: keep control alive while a separate socket waits for video."""
    def __enter__(self):
        self.socket = socket.create_connection(('127.0.0.1', 27185), timeout=8)
        self.lock = threading.Lock()
        self.stop = threading.Event()
        send(self, dict(type='hello', version=1))
        self.ready = read(self)
        assert self.ready.get('type') == 'ready', 'Управляющий сеанс не принят'
        self.thread = threading.Thread(target=self.heartbeat, daemon=True)
        self.thread.start()
        return self

    def sendall(self, data):
        with self.lock:
            self.socket.sendall(data)

    def recv(self, count):
        return self.socket.recv(count)

    def heartbeat(self):
        while not self.stop.wait(1):
            try:
                send(self, dict(type='ping', sent=int(time.monotonic() * 1_000_000)))
            except OSError:
                return

    def __exit__(self, *_):
        self.stop.set()
        self.socket.close()
        self.thread.join(timeout=2)


def wait_pong(control, stamp):
    for _ in range(64):
        message = read(control)
        if message.get('type') == 'pong' and message.get('sent') == stamp:
            return
    raise AssertionError('Управление не отвечает во время видеопотока')


def gesture_stats():
    for node in ui().iter('node'):
        text = node.get('text', '')
        if text.startswith('Нажатий: '):
            return tuple(map(int, re.findall(r'\d+', text)))
    raise AssertionError('Не видны счётчики отдельной цели жестов')


def gesture_regression(fixture):
    adb('install', '-r', fixture, timeout=120)
    adb('shell', 'am', 'start', '-W', '-n', 'ru.lenscast.gesturefixture/.TargetActivity')
    root = ui()
    target = next((n for n in root.iter('node') if n.get('resource-id') == 'ru.lenscast.gesturefixture:id/gesture_target'), None)
    assert target is not None, 'Не найдена кнопка отдельной цели жестов по resource-id'
    x1, y1, x2, y2 = map(int, re.findall(r'\d+', target.get('bounds', '')))
    size = re.search(r'Physical size: (\d+)x(\d+)', adb('shell', 'wm', 'size'))
    assert size, 'Нет размеров дисплея цели'
    width, height = map(int, size.groups())
    x, y = ((x1+x2)/2)/(width-1), ((y1+y2)/2)/(height-1)

    def touch(action, nx=x, ny=y):
        send(control, dict(type='touch', action=action, x=nx, y=ny))

    def settled():
        send(control, dict(type='ping', sent=790))
        wait_pong(control, 790)
        time.sleep(0.15)
        return gesture_stats()

    with ControlSession() as control:
        # The previous implementation cancelled a zero-distance continuation before UP.
        touch('down'); time.sleep(0.08); touch('up')
        clicks, holds, ups, moves, duration, active = settled()
        assert clicks == 1 and holds == 0 and ups == 1 and active == 0 and duration < 500, 'Короткое нажатие не стало обычным тапом'
        print('Пройдено: настоящий Android OnClick/ACTION_UP после короткого тапа, без OnLongClick')

        touch('down')
        for _ in range(16):
            time.sleep(0.2); touch('move')  # Stationary desktop heartbeat must NOT cancel a stroke.
        touch('up')
        clicks, holds, ups, moves, duration, active = settled()
        assert clicks == 1 and holds == 1 and ups == 2 and active == 0 and duration >= 2800, 'Удержание или отпускание сломано'
        print('Пройдено: неподвижное удержание и heartbeat дают один OnLongClick и реальный ACTION_UP')

        touch('down'); time.sleep(0.05)
        for i in range(1, 6):
            touch('move', x+i*0.02, y); time.sleep(0.04)
        touch('up', x+0.1, y)
        stats = settled()
        assert stats[2] == 3 and stats[3] > 0 and stats[5] == 0, 'Продолжаемое перетаскивание не отпустило палец'
        baseline_clicks, baseline_holds = stats[0:2]

        touch('down'); time.sleep(2.0)  # Control pings continue, pointer heartbeat does not.
        stats = settled()
        assert stats[2] == 4 and stats[5] == 0 and stats[1] == baseline_holds+1, 'Таймер безопасности не отпустил неподвижный жест'
        touch('down'); time.sleep(0.08); touch('up')
        stats = settled()
        assert stats[0] == baseline_clicks+1 and stats[2] == 5 and stats[5] == 0, 'Тап после безопасного отпускания не работает'
        print('Пройдено: живой drag, отпускание по watchdog и следующий тап')
        touch('down'); time.sleep(0.08)
        # Closing the real control socket must finish the outstanding stroke.
    time.sleep(0.25)
    stats = gesture_stats()
    assert stats[2] == 6 and stats[5] == 0, 'Разрыв управляющего сокета оставил палец нажатым'
    print('Пройдено: разрыв управления отпускает настоящий палец Android')
    adb('shell', 'am', 'start', '-W', '-n', 'ru.lenscast/.MainActivity')


def screen_smoke(apk, fixture):
    start(apk)
    adb('shell', 'settings', 'put', 'secure', 'enabled_accessibility_services', 'ru.lenscast/ru.lenscast.GestureService')
    adb('shell', 'settings', 'put', 'secure', 'accessibility_enabled', '1')
    adb('forward', 'tcp:27186', 'tcp:27186')
    for _ in range(12):
        node = next((n for n in ui().iter('node') if n.get('text') == 'Разрешить показ экрана' and n.get('enabled') == 'true'), None)
        if node is not None and tap_node(node):
            break
        adb('shell', 'input', 'swipe', '500', '1550', '500', '500', '300')
    else:
        raise AssertionError('Недоступна кнопка разрешения экрана')
    for _ in range(8):
        root = ui()
        consent = next((n for n in root.iter('node') if n.get('enabled') == 'true' and
                        (n.get('resource-id', '') == 'android:id/button1' or n.get('resource-id', '').endswith('/start_button') or
                         n.get('text', '').lower() in ('start now', 'start', 'начать', 'начать сейчас'))), None)
        if consent is not None:
            tap_node(consent)
        if any(n.get('text', '').startswith('Экран передаётся') for n in root.iter('node')):
            break
        time.sleep(1)
    gesture_regression(fixture)
    with ControlSession() as control:
        ready = control.ready
        ticket = ready.get('videoTicket', '')
        assert len(ticket) == 64, 'Нет привязки видеоканала к сеансу'
        with attach_video('0' * 64) as rejected:
            assert rejected.recv(1) == b'', 'Видео доступно без аутентификации'
        with attach_video(ticket) as video:
            wait_key(video)
            for _ in range(3):
                video_packet(video)
            print('Пройдено: MediaProjection/MediaCodec, настоящий Annex B, отдельный защищённый сеанс видео')
            send(control, dict(type='navigation', action='home'))
            send(control, dict(type='ping', sent=789))
            wait_pong(control, 789)
            time.sleep(1)
            send(control, dict(type='touch', action='down', x=0.1, y=0.1))
            send(control, dict(type='touch', action='move', x=0.15, y=0.1))
            send(control, dict(type='touch', action='up', x=0.15, y=0.1))
            # The Pixel launcher can force portrait. Resume the resizable test app
            # before requesting rotation, without restarting the projection service.
            adb('shell', 'am', 'start', '-W', '-n', 'ru.lenscast/.MainActivity')
            adb('shell', 'settings', 'put', 'system', 'accelerometer_rotation', '0')
            adb('shell', 'settings', 'put', 'system', 'user_rotation', '1')
            send(control, dict(type='keyframe'))
            wait_key(video, landscape=True)
            print('Пройдено: поток продолжается на домашнем экране и после поворота без нового согласия')
        send(control, dict(type='touch', action='down', x=0.1, y=0.1))
    time.sleep(1)
    with ControlSession() as control:
        ticket2 = control.ready['videoTicket']
        assert ticket2 != ticket, 'Повторно использован билет старого сеанса'
        with attach_video(ticket) as rejected:
            assert rejected.recv(1) == b'', 'Старый билет видео не отозван'
        with attach_video(ticket2) as video:
            wait_key(video)
        send(control, dict(type='stop'))
    adb('shell', 'settings', 'put', 'system', 'user_rotation', '0')
    print('Пройдено: переподключение сохраняет захват, но меняет и проверяет билет видео')


def camera_smoke(apk):
    start(apk, mode='Веб-камера')
    adb('shell', 'pm', 'grant', 'ru.lenscast', 'android.permission.CAMERA')
    for _ in range(12):
        node = next((n for n in ui().iter('node') if n.get('text') == 'Запустить камеру' and n.get('enabled') == 'true'), None)
        if node is not None and tap_node(node):
            break
        adb('shell', 'input', 'swipe', '500', '1550', '500', '500', '300')
    else:
        raise AssertionError('Кнопка запуска камеры недоступна')

    with ControlSession() as control:
        assert control.ready['mode'] == 'Веб-камера'
        ticket = control.ready['videoTicket']
        with attach_video(ticket) as video:
            # Drain concurrently: UI/metadata operations must not back up the video socket.
            done = threading.Event()
            packets = []
            received = [0]
            video_config = threading.Event()
            video_key = threading.Event()
            video_epoch = [0]
            key_epoch = [0]
            failure = []
            def drain():
                try:
                    while not done.is_set():
                        kind, body = video_packet(video)
                        packets.append((kind, len(body)))
                        if kind in (1, 2): received[0] += 1
                        if kind == 0:
                            video_epoch[0] += 1
                            video_config.set()
                        if kind == 2:
                            key_epoch[0] = video_epoch[0]
                            video_key.set()
                        del packets[:-300]
                except Exception as e:
                    if not done.is_set(): failure.append(e)
            thread = threading.Thread(target=drain, daemon=True)
            thread.start()
            try:
                def state(predicate=lambda c: c.get('ready'), timeout=25):
                    deadline = time.monotonic()+timeout
                    last = {}
                    while time.monotonic()<deadline:
                        message = read(control)
                        if message.get('type') == 'telemetry':
                            assert not message['screen'], 'Режим камеры запустил MediaProjection'
                            c = message.get('camera', {})
                            last = {key: c.get(key) for key in ('generation', 'running', 'ready', 'proof', 'status')}
                            if predicate(c): return c
                    raise AssertionError('Камера не выдала ожидаемое состояние: '+json.dumps(last, ensure_ascii=False)+'; ошибка чтения видео='+','.join(type(e).__name__ for e in failure))
                c = state(lambda s: s.get('ready') and video_config.is_set() and video_key.is_set())
                assert c['modes'] and c['cameras'] and video_config.is_set() and video_key.is_set(), 'Нет настоящих конфигурации и ключевого кадра камеры'
                assert len(json.dumps(c).encode()) < 60000, 'Каталог не помещается в управляющий кадр'
                print('Пройдено: Camera2 → поверхность MediaCodec → настоящий Annex B; MediaProjection не запущен')
                controls = {item['key']: item for item in c['controls']}
                for key in ('zoom', 'ev', 'aeLock', 'awbLock', 'awbMode', 'focusMode', 'torch'):
                    if key not in controls: continue
                    item = controls[key]
                    value = False if item.get('boolean') else item['values'][-1] if 'values' in item else item['min']
                    send(control, dict(type='cameraSet', controls={key: value}))
                    c = state(lambda s: s.get('ready') and any(i.get('key') == key and i.get('value') == value for i in s.get('controls', [])))
                if all(key in controls for key in ('manualExposure', 'iso', 'exposureMs')):
                    iso = int(controls['iso']['min'])
                    exposure = max(controls['exposureMs']['min'], min(10.0, controls['exposureMs']['max']))
                    send(control, dict(type='cameraSet', controls={'manualExposure': True, 'iso': iso, 'exposureMs': exposure}))
                    state(lambda s: s.get('ready') and s.get('actualIso') == iso and abs(s.get('actualExposureMs', -100)-exposure) < 0.5)
                    send(control, dict(type='cameraSet', controls={'manualExposure': False}))
                    state(lambda s: s.get('ready') and any(i.get('key') == 'manualExposure' and i.get('value') is False for i in s.get('controls', [])))
                    print('Пройдено: ручные ISO/выдержка подтверждены реальным CaptureResult, возврат auto работает')
                # Invalid input must leave capture alive and must not be persisted.
                send(control, dict(type='cameraSet', controls={'несуществующий': 1}))
                state(lambda s: s.get('ready') and s.get('status', '').startswith('Настройка не применена'))
                for _ in range(3):
                    generation = c['generation']
                    previous_video = video_epoch[0]
                    send(control, dict(type='cameraSet', reset=True))
                    c = state(lambda s: s.get('ready') and s.get('generation', -1) > generation and
                              video_epoch[0] > previous_video and key_epoch[0] == video_epoch[0])
                    assert all(c['proof'][key] for key in ('capture', 'config', 'key')), 'Готовность без полного подтверждения текущей сессии'
                    print('Пройдено: сброс камеры, поколение', c['generation'], 'новые CaptureResult/CSD/IDR и кадры в реальном видеосокете')
                print('Пройдено: доступные Camera2-параметры применяются; неверный параметр не ломает поток; сброс работает')
                old_id = c['cameraId']
                mode = next((m for m in reversed(c['modes']) if m['id'] != c['mode']), None)
                if mode:
                    send(control, dict(type='cameraSet', mode=mode['id']))
                    c = state(lambda s: s.get('ready') and s.get('mode') == mode['id'])
                other = next((camera for camera in c['cameras'] if camera['id'] != old_id), None)
                if other:
                    send(control, dict(type='cameraSet', cameraId=other['id']))
                    c = state(lambda s: s.get('ready') and s.get('cameraId') == other['id'])
                send(control, dict(type='cameraSet', rotation=180, mirror=True))
                state(lambda s: s.get('ready') and s.get('rotation') == 180 and s.get('mirror'))
                print('Пройдено: реальная пересборка сессии при смене режима/модуля, поворот и отражение')
                # Start with the activity visible, then exercise the real power key.
                before_sleep = received[0]
                adb('shell', 'input', 'keyevent', '223')  # KEYCODE_SLEEP
                time.sleep(3)
                c = state()
                assert not failure and received[0] > before_sleep+2, 'Камера перестала выдавать видео с выключенным дисплеем'
                adb('shell', 'input', 'keyevent', '224')  # KEYCODE_WAKEUP
                adb('shell', 'wm', 'dismiss-keyguard')
                print('Пройдено: камера продолжает выдавать кадры с выключенным экраном AVD')
                adb('shell', 'am', 'start', '-W', '-n', 'ru.lenscast/.MainActivity')
                for _ in range(10):
                    black = next((n for n in ui().iter('node') if n.get('text') == 'Чёрный экран'), None)
                    if black is not None and tap_node(black): break
                    adb('shell', 'input', 'swipe', '500', '1550', '500', '500', '300')
                else: raise AssertionError('Чёрный fallback недоступен')
                before_black = received[0]
                root = ui()
                back = next((n for n in root.iter('node') if n.get('text') == 'Вернуться'), None)
                assert back is not None and received[0] > before_black, 'Fallback не сохранил камеру и кнопку возврата'
                assert tap_node(back), 'Не сработал возврат из чёрного экрана'
                state()
                print('Пройдено: чёрный fallback и возврат не останавливают настоящий поток камеры')

            finally:
                done.set()
                video.shutdown(socket.SHUT_RDWR)
                thread.join(timeout=3)
        saved_id, saved_mode = c['cameraId'], c['mode']
    time.sleep(1)
    with ControlSession() as control:
        assert control.ready['videoTicket'] != ticket
        with attach_video(control.ready['videoTicket']) as video:
            wait_key(video)
        send(control, dict(type='stop'))
    # Recreate service and verify successful settings survive, without remembering a PC.
    time.sleep(1)
    start(apk, mode='Веб-камера')
    for _ in range(10):
        node = next((n for n in ui().iter('node') if n.get('text') == 'Запустить камеру'), None)
        if node is not None and tap_node(node): break
        adb('shell', 'input', 'swipe', '500', '1550', '500', '500', '300')
    else: raise AssertionError('Камера не может быть запущена повторно')
    with ControlSession() as control:
        with attach_video(control.ready['videoTicket']) as video:
            wait_key(video)
        for _ in range(20):
            message = read(control)
            if message.get('type') == 'telemetry' and message.get('camera', {}).get('ready'):
                saved = message['camera']
                assert saved['cameraId'] == saved_id and saved['mode'] == saved_mode and saved['rotation'] == 180 and saved['mirror'], 'Параметры камеры не восстановлены'
                break
        else: raise AssertionError('Нет подтверждения повторного запуска камеры')
        send(control, dict(type='stop'))
    print('Пройдено: переподключение камеры, отзыв билета, остановка и восстановление настроек после нового старта')


def main():
    apk, legacy, fixture = sys.argv[1:4]
    remembered = exercise(apk)
    # Real package update must keep an already-compatible identity and its trust.
    exercise(apk, remembered=remembered, tls12=True)
    print('Пройдено: обновление APK сохраняет сертификат и токен; TLS 1.2 работает')
    adb('uninstall', 'ru.lenscast')
    start(legacy)  # Generate the original immutable key under the application's UID.
    try:
        with socket.create_connection(('127.0.0.1', 27183), timeout=8) as tcp:
            with tls_context().wrap_socket(tcp, server_hostname='Lenscast'):
                pass
    except ssl.SSLError:
        print('Воспроизведено: исходный APK со старыми разрешениями ключа обрывает TLS')
    else:
        raise AssertionError('Контрольный старый APK неожиданно прошёл TLS')
    exercise(apk)  # No uninstall/data reset: migration must repair the old key.
    print('Пройдено: обновление поверх старого APK исправляет TLS без удаления данных')
    screen_smoke(apk, fixture)
    camera_smoke(apk)


if __name__ == '__main__':
    try:
        main()
    except Exception:
        # Capture before the action shuts the emulator down; a later adb logcat
        # would otherwise wait forever for a device which no longer exists.
        try:
            subprocess.run(['adb','logcat','-d','-s','Lenscast:I','AndroidRuntime:E'], timeout=15, check=False)
        except subprocess.TimeoutExpired:
            pass
        raise
