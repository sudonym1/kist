"""Build matching Setup/Update payloads for isolated Windows M7 installations."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile
from stage_journal_runtime import verify
import subprocess
import sys


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--build', type=Path, required=True)
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit('Output must be new; preserve previous installers.')
    source = json.loads((args.baseline / 'payload/payload-manifest.json').read_text(encoding='utf-8-sig'))
    payload = args.output / 'payload'
    for entry in source['Files']:
        relative = Path(entry['Path'])
        if relative.parts[:2] == ('BepInEx', 'plugins'):
            continue
        original = args.baseline / 'payload' / relative
        if sha(original) != entry['Sha256']:
            raise SystemExit(f'Baseline checksum mismatch: {relative}')
        target = payload / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, target)
    plugins = payload / 'BepInEx/plugins'
    subprocess.run([sys.executable, str(Path(__file__).with_name('stage_journal_runtime.py')), str(args.build), str(args.packages), str(plugins), '--rids', 'win-x64'], check=True)
    verify(plugins)
    shutil.copy2(args.build / 'ThorsKist.AdapterTests.dll', plugins)
    script_root = Path(__file__).resolve().parent
    for mode, script in [('Setup', 'setup-manual-test.ps1'), ('Update', 'update-manual-test.ps1')]:
        shutil.copy2(script_root / script, args.output / (mode + '.ps1'))
        (args.output / (mode + '.cmd')).write_text(f'@echo off\npowershell -NoProfile -File "%~dp0{mode}.ps1"\npause\n', encoding='ascii')
    source['Authority'] = 'SQLite/journal only; protocol 5; legacy import once; fail closed'
    source['Files'] = [{'Path': str(p.relative_to(payload)), 'Sha256': sha(p)} for p in sorted(payload.rglob('*')) if p.is_file()]
    (payload / 'payload-manifest.json').write_text(json.dumps(source, indent=2), encoding='utf-8')
    (args.output / 'READ-ME-FIRST.txt').write_text(
        'M7 WINDOWS JOURNAL CUTOVER CANDIDATE\n'
        'Close the separate test game, then use Update.cmd for an existing M7 installation.\n'
        'Setup.cmd creates a new separate full game copy. Test saves are preserved by Update.\n'
        'The main game and original saves must never be selected. Steam cloud stays hidden in the test process.\n'
        'Both PCs must use this exact payload and matching Steam game builds.\n'
        'Startup must report KIST AUTHORITY READY SQLite/journal on the host.\n'
        'The current world .dat is backed up and imported once; subsequent opens use SQLite exclusively.\n'
        'Provider, migration and commit errors close Kist access. Never delete SQLite to recover from an error.\n'
        'F4 records frames plus bounded journal transfer timings; F4 again stops and exports on a worker.\n'
        'BepInEx/timings/<capture>/journal-events.csv links requests and sequence numbers across queues, SQLite, effects and acknowledgments.\n'
        'journal-stages.csv and journal-summary.txt report thread violations and synchronous main-thread work above 1 ms. Kist drawing is outside this journal trace.\n'
        'Use ordinary F3 Kist actions for journal gameplay. Wait for TIMING SAVED before exiting.\n'
        'Await the validation handoff before manual testing. Diagnostic fixture keys are not gameplay evidence.\n'
        'Logs: BepInEx/LogOutput.log and Player.log. Production min hird size remains 2; isolated tests use 1.\n', encoding='utf-8')
    archive = args.output.with_suffix('.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for p in sorted(args.output.rglob('*')):
            if p.is_file():
                z.write(p, str(p.relative_to(args.output)))
    print(json.dumps({'bundle': str(archive), 'sha256': sha(archive), 'files': len(source['Files']), 'authority': source['Authority']}))


if __name__ == '__main__':
    main()
