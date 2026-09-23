#!/usr/bin/env python3
"""Export measured campaign evidence, excluding private state and credentials.

Run after campaigns stop. Refuses symbolic/special files, excessive data and any
occurrence of this installation's actual token/password bytes. Backup payloads,
connection environments and build work directories never enter the archive.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tarfile


def require(value, message):
    if not value:
        raise ValueError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path.home() / 'services/fogell'
    require(os.uname().nodename == 'luigi', 'Luigi only')
    require(args.output.is_absolute() and args.output.is_relative_to(root / 'exports'), 'owned export path required')
    args.output.mkdir(parents=True, mode=0o700, exist_ok=False)
    secret_values = [(root / 'private/token').read_bytes().strip()]
    environment = (root / 'private/controller.env').read_bytes()
    secret_values += re.findall(rb'(?:^|;)Password=([^;\r\n]+)', environment)
    require(len(secret_values) == 3 and all(len(v) >= 32 for v in secret_values), 'private secret inventory incomplete')
    selected = {}
    total = 0
    def add(path, relative):
        nonlocal total
        info = path.lstat()
        require(stat.S_ISREG(info.st_mode), 'evidence contains a nonregular file')
        require(info.st_size <= 256 * 1024**2, 'individual evidence file exceeds bound')
        data = path.read_bytes()
        require(not any(secret in data for secret in secret_values), 'private credential found in evidence; export refused')
        total += len(data)
        require(total <= 1024**3 and len(selected) < 20000, 'aggregate evidence exceeds bound')
        selected[str(relative)] = {'path': path, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
    for name in ['deployment.json', 'protected-before.json']:
        add(root / name, Path(name))
    for parent, directories, files in os.walk(root / 'campaigns', followlinks=False):
        for name in directories:
            require(not (Path(parent) / name).is_symlink(), 'evidence directory symlink refused')
        directories[:] = [name for name in directories if not (name.endswith('-work') or name == 'work')]
        for name in files:
            path = Path(parent) / name
            add(path, path.relative_to(root))
    for backup in sorted((root / 'backups').iterdir()):
        require(backup.is_dir() and not backup.is_symlink(), 'unexpected backup directory')
        for name in ['receipt.json', 'pair-manifest.json', 'workload.json', 'volatile-cleanup.json', 'stale-authority.json']:
            path = backup / name
            if path.exists():
                add(path, path.relative_to(root))
    archive = args.output / 'campaigns.tgz'
    with tarfile.open(archive, 'w:gz') as tar:
        for relative, row in sorted(selected.items()):
            # Verify again just before packaging; never certify a changing file.
            require(hashlib.sha256(row['path'].read_bytes()).hexdigest() == row['sha256'], 'evidence changed during export')
            tar.add(row['path'], arcname=relative, recursive=False)
    manifest = {'schema_version': 1, 'archive': archive.name,
                'archive_sha256': hashlib.sha256(archive.read_bytes()).hexdigest(),
                'uncompressed_bytes': total, 'private_credentials_absent': True,
                'files': {name: {k: v for k, v in row.items() if k != 'path'} for name, row in selected.items()}}
    (args.output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps({'archive': str(archive), 'sha256': manifest['archive_sha256'],
                      'files': len(selected), 'uncompressed_bytes': total}))


if __name__ == '__main__':
    main()
