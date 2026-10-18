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
import xml.etree.ElementTree as ET


def adb(*args):
    return subprocess.check_output(['adb', *args], timeout=30).decode('utf-8', 'replace')


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


def start(apk):
    adb('install', '-r', apk)
    sdk = int(adb('shell', 'getprop', 'ro.build.version.sdk').strip())
    if sdk >= 33:
        adb('shell', 'pm', 'grant', 'ru.lenscast', 'android.permission.POST_NOTIFICATIONS')
    adb('shell', 'am', 'start', '-W', '-n', 'ru.lenscast/.MainActivity')
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


def main():
    apk, legacy = sys.argv[1:3]
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
