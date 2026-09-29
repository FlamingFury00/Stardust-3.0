#!/usr/bin/env python3
"""Package the committed source or a tested Bob output directory, without local logs/build debris."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tomllib
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PREFIX = 'Stardust-3.0/'


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--revision', default='HEAD')
    parser.add_argument('--binaries', type=Path, help='Bob output containing the tested platform folders/config')
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError(f'Refusing to overwrite {output}')
    revision = git('rev-parse', '--verify', '--end-of-options', args.revision + '^{commit}').decode().strip()
    if git('ls-tree', '-r', revision).find(b'160000 commit ') >= 0:
        raise ValueError('Source contains submodules; vendor their required contents before packaging')
    output.parent.mkdir(parents=True, exist_ok=True)
    if args.binaries is None:
        subprocess.run(['git', 'archive', '--format=zip', '--prefix=' + PREFIX,
                        '--output=' + str(output), revision], cwd=ROOT, check=True)
        config = git('show', revision + ':Stardust.bot.toml')
    else:
        binary_root = args.binaries.resolve(strict=True)
        config = (binary_root / 'Stardust.bot.toml').read_bytes()
        with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for path in sorted(binary_root.rglob('*')):
                relative = path.relative_to(binary_root)
                if any(p.lower() in ('logs', 'log', '.git') for p in relative.parts) or path.suffix.lower() in ('.pdb', '.dbg'):
                    continue
                if path.is_symlink():
                    raise ValueError(f'Unexpected symlink in binary output: {relative}')
                if path.is_file():
                    archive.write(path, PREFIX + relative.as_posix())
    parsed = tomllib.loads(config.decode('utf-8-sig'))
    required = ('agent_id', 'name', 'run_command', 'loadout_file')
    if any(not parsed['settings'].get(key) for key in required):
        raise ValueError('Incomplete bot configuration')
    info = {'source_commit': revision, 'kind': 'runtime' if args.binaries else 'source',
            'framework': 'RLBot v5', 'agent_id': parsed['settings']['agent_id'],
            'config': 'Stardust.bot.toml', 'config_alias': 'bot.toml'}
    with zipfile.ZipFile(output, 'a', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        entries = set(archive.namelist())
        for path in ('LICENSE', 'docs/RLBC_2026.md', 'rlbc2026/rookie-proof.match.toml'):
            if PREFIX + path not in entries:
                archive.writestr(PREFIX + path, git('show', revision + ':' + path))
        if PREFIX + 'bot.toml' not in entries:
            archive.writestr(PREFIX + 'bot.toml', config)
        archive.writestr(PREFIX + 'BUILD_INFO.json', json.dumps(info, indent=2) + '\n')
        if archive.testzip() is not None:
            raise ValueError('Archive CRC validation failed')
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix(output.suffix + '.sha256').write_text(f'{digest}  {output.name}\n', encoding='utf-8')
    print(json.dumps({'path': str(output), 'bytes': output.stat().st_size, 'sha256': digest,
                      'source_commit': revision}))


if __name__ == '__main__':
    main()
