"""Export bounded startup diagnostics on CI failure; never credentials or prefs."""
import os
import sys
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path

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
