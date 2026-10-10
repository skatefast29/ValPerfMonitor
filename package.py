"""Builds a Thunderstore-format zip that Gale can import (Profile > Import > Local mod).

Usage: python package.py   (after: dotnet build -c Release)
Output: dist/ValPerfMonitor-<version>.zip
"""
import json
import os
import re
import struct
import zipfile
import zlib

ROOT = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(ROOT, 'bin', 'Release', 'net472', 'ValPerfMonitor.dll')
PRELOADER = os.path.join(ROOT, 'preloader', 'bin', 'Release', 'net472', 'ValPerfMonitor.Preloader.dll')
VERSION = re.search(r'<Version>([^<]+)</Version>', open(os.path.join(ROOT, 'ValPerfMonitor.csproj'), encoding='utf-8').read()).group(1)


def icon_png(size=256):
    """A simple 256x256 icon: dark background with an orange frame-time bar graph."""
    bars = [0.35, 0.55, 0.4, 0.8, 0.5, 0.95, 0.45, 0.6]
    rows = []
    for y in range(size):
        row = bytearray([0])  # filter type 0
        for x in range(size):
            r, g, b = 24, 26, 32
            if 28 <= x < 228 and 40 <= y < 216:
                i = (x - 28) // 25
                h = int(bars[i] * 160)
                if (x - 28) % 25 < 19 and y >= 216 - h:
                    r, g, b = (255, 140, 40) if bars[i] < 0.9 else (235, 70, 70)
            if y in (101, 102) and 28 <= x < 228:
                r, g, b = 90, 200, 110  # 60 fps line
            row += bytes((r, g, b))
        rows.append(bytes(row))
    raw = zlib.compress(b''.join(rows), 9)

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)

    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 2, 0, 0, 0)) + chunk(b'IDAT', raw) + chunk(b'IEND', b'')


manifest = {
    "name": "ValPerfMonitor",
    "version_number": VERSION,
    "website_url": "https://github.com/skatefast29/ValPerfMonitor",
    "description": "Testing tool: measures the CPU time and memory each mod and game system costs, with a test panel (F11), CSV logs and HTML reports.",
    "dependencies": ["denikson-BepInExPack_Valheim-5.4.2351"],
}

os.makedirs(os.path.join(ROOT, 'dist'), exist_ok=True)
out = os.path.join(ROOT, 'dist', f'ValPerfMonitor-{VERSION}.zip')
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('manifest.json', json.dumps(manifest, indent=2))
    z.writestr('icon.png', icon_png())
    z.write(os.path.join(ROOT, 'README.md'), 'README.md')
    z.write(os.path.join(ROOT, 'CHANGELOG.md'), 'CHANGELOG.md')
    z.write(DLL, 'plugins/ValPerfMonitor.dll')
    z.write(PRELOADER, 'patchers/ValPerfMonitor.Preloader.dll')
print(out)
