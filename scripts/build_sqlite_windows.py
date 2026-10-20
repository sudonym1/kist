#!/usr/bin/env python3
"""Build SQLite 3.54.0 x64 with an optimized static CRT and matching full PDB.

Requires MSVC x64 tools and Windows SDK. No installed-game or NuGet-cache writes.
Usage: python scripts/build_sqlite_windows.py --vcvars VCVARSALL.BAT
       [--output .tools/sqlite-symbols/build]
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import urllib.request
import zipfile
from native_symbols import verify_pdb

URL = 'https://sqlite.org/2026/sqlite-amalgamation-3540000.zip'
SHA256 = '68e913b0fe8ec6b4e5f391c594a28872884e80d6cd042a1e50c957be46b5aa4d'
DEFINES = [
    'NDEBUG', 'SQLITE_API=__declspec(dllexport)', 'SQLITE_THREADSAFE=1',
    'SQLITE_DEFAULT_AUTOVACUUM=1', 'SQLITE_DEFAULT_FOREIGN_KEYS=1',
    'SQLITE_DEFAULT_RECURSIVE_TRIGGERS=1', 'SQLITE_DQS=0',
    'SQLITE_ENABLE_COLUMN_METADATA', 'SQLITE_ENABLE_FTS3',
    'SQLITE_ENABLE_FTS3_PARENTHESIS', 'SQLITE_ENABLE_FTS4', 'SQLITE_ENABLE_FTS5',
    'SQLITE_ENABLE_GEOPOLY', 'SQLITE_ENABLE_MATH_FUNCTIONS',
    'SQLITE_ENABLE_PREUPDATE_HOOK', 'SQLITE_ENABLE_RTREE',
    'SQLITE_ENABLE_SESSION', 'SQLITE_ENABLE_SNAPSHOT',
    'SQLITE_LIKE_DOESNT_MATCH_BLOBS', 'SQLITE_MAX_DEFAULT_PAGE_SIZE=8192',
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--vcvars', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('.tools/sqlite-symbols/build'))
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        parser.error('Build output already exists; choose a fresh directory.')
    vcvars = args.vcvars.resolve(strict=True)
    source = urllib.request.urlopen(URL, timeout=60).read()
    if hashlib.sha256(source).hexdigest() != SHA256:
        raise ValueError('SQLite source archive checksum mismatch')
    output.mkdir(parents=True)
    (output / 'sqlite-amalgamation-3540000.zip').write_bytes(source)
    with zipfile.ZipFile(io.BytesIO(source)) as archive:
        for name in ('sqlite3.c', 'sqlite3.h'):
            (output / name).write_bytes(archive.read('sqlite-amalgamation-3540000/' + name))
    options = ['/nologo', '/O2', '/Z7', '/MT', '/W3', '/c', 'sqlite3.c']
    options += ['/D' + d for d in DEFINES]
    (output / 'compile.rsp').write_text('\n'.join('"' + s + '"' for s in options), encoding='ascii')
    command = (f'@echo off\ncall "{vcvars}" x86_amd64\nif errorlevel 1 exit /b 1\n'
               'cl @compile.rsp\nif errorlevel 1 exit /b 1\n'
               'link /nologo /DLL /MACHINE:X64 /DEBUG:FULL /INCREMENTAL:NO '
               '/OPT:REF /OPT:ICF /OUT:e_sqlite3.dll /PDB:e_sqlite3.pdb '
               '/PDBALTPATH:e_sqlite3.pdb sqlite3.obj\nexit /b %errorlevel%\n')
    (output / 'build.cmd').write_text(command, encoding='ascii')
    subprocess.run(['cmd.exe', '/d', '/c', str(output / 'build.cmd')], cwd=output, check=True)
    symbols = verify_pdb(output / 'e_sqlite3.dll', output / 'e_sqlite3.pdb')
    receipt = {'schema': 1, 'sqlite_version': '3.54.0', 'source_url': URL,
               'source_sha256': SHA256, 'compile_options': options,
               'symbols': symbols, 'sha256': {name: hashlib.sha256((output / name).read_bytes()).hexdigest()
                                             for name in ('e_sqlite3.dll', 'e_sqlite3.pdb')}}
    (output / 'sqlite-build.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print('Built optimized SQLite x64 DLL with matching native PDB: ' + str(output))


if __name__ == '__main__':
    main()
