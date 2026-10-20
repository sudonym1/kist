#!/usr/bin/env python3
"""Stage and validate a release ZIP from an existing build; never publish it.

Usage: python scripts/prepare_hexium.py BUILD_DIRECTORY NUGET_PACKAGES
       [--output dist] [--rids win-x64 linux-x64]
BUILD_DIRECTORY must contain the complete managed runtime (for example the
tools/AdapterTests Release output). Test plugins are never included.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('build', type=Path)
    parser.add_argument('packages', type=Path)
    parser.add_argument('--output', type=Path, default=Path('dist'))
    parser.add_argument('--rids', nargs='+', default=['win-x64', 'linux-x64'])
    parser.add_argument('--sqlite-windows-build', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    manifest = json.loads((root / 'manifest.json').read_text(encoding='utf-8-sig'))
    version = manifest['version_number']
    candidate = args.output.resolve() / f"{manifest['name']}-{version}"
    archive = candidate.parent / (candidate.name + '.zip')
    if candidate.exists() or archive.exists():
        parser.error('Release destination already exists; preserve it and choose a new output directory.')
    subprocess.run([sys.executable, str(root / 'scripts/version.py'), 'check'], cwd=root, check=True)
    candidate.mkdir(parents=True)
    for name in ('manifest.json', 'README.md', 'CHANGELOG.md', 'icon.png',
                 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'hird1.png', 'kist1.png'):
        shutil.copy2(root / name, candidate / name)
    shutil.copytree(root / 'licenses', candidate / 'licenses')
    native_options = (['--sqlite-windows-build', str(args.sqlite_windows_build.resolve())]
                      if args.sqlite_windows_build else [])
    subprocess.run([sys.executable, str(root / 'scripts/stage_journal_runtime.py'),
                    str(args.build.resolve()), str(args.packages.resolve()),
                    str(candidate / 'plugins'), '--rids', *args.rids, *native_options], check=True)
    subprocess.run([sys.executable, str(root / 'scripts/package_release.py'),
                    str(candidate), version], check=True)
    subprocess.run([sys.executable, str(root / 'scripts/verify_hexium.py'),
                    str(archive), version], check=True)
    with zipfile.ZipFile(archive) as payload:
        if payload.testzip() is not None:
            raise ValueError('ZIP CRC verification failed')
        for path in candidate.rglob('*'):
            if path.is_file() and payload.read(path.relative_to(candidate).as_posix()) != path.read_bytes():
                raise ValueError('ZIP differs from verified staging: ' + str(path))
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix('.zip.sha256').write_text(f'{digest}  {archive.name}\n', encoding='ascii')
    print(f'Upload ZIP: {archive}\nSHA256: {digest}')


if __name__ == '__main__':
    main()
