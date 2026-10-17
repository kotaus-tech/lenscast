"""Export small, non-secret build manifests through check annotations (3 KB chunks).

This also keeps useful startup diagnostics accessible when artifact downloads are
not available to the development environment. Never export credentials or prefs.
"""
import base64
import os
import sys
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path

files = [
    Path('android/app/gradle.lockfile'),
    Path('windows/Lenscast/packages.lock.json'),
    Path('tests/Lenscast.Tests/packages.lock.json'),
]
for path in files:
    if not path.exists():
        continue
    encoded = base64.b64encode(path.read_bytes()).decode('ascii')
    for index, start in enumerate(range(0, len(encoded), 3000)):
        print(f'::notice title=manifest:{path.as_posix()}:{index:04}::{encoded[start:start+3000]}')
if os.name == 'nt':
    log = Path(os.environ['LOCALAPPDATA']) / 'Lenscast' / 'журнал.txt'
    if log.exists():
        text = log.read_text(encoding='utf-8')[-12000:]
        for index, start in enumerate(range(0, len(text), 2000)):
            chunk = text[start:start+2000].replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
            print(f'::notice title=startup-log:{index:04}::{chunk}')
    for path in [Path('artifacts/smoke-stderr.txt'), Path('artifacts/smoke-stdout.txt')]:
        if path.exists():
            text = path.read_text(encoding='utf-8', errors='replace')[-12000:]
            for index, start in enumerate(range(0, len(text), 2000)):
                chunk = text[start:start+2000].replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
                print(f'::notice title={path.name}:{index:04}::{chunk}')
