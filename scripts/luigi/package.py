#!/usr/bin/env python3
"""Build and package committed Fogell source and its exact Release binaries.

Run on HeMan after the full local gate. This records identity, not gate success.
The output directory must be new. Package caches and credentials are excluded.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile

SOURCE = ['src', 'tools', 'tests', 'scripts', 'Directory.Build.props',
          'Directory.Build.targets', 'Directory.Packages.props', 'Fogell.slnx', 'global.json']
BINARIES = ['src/Fogell.Controller.Host', 'tools/Fogell.Run.Host',
            'tools/Fogell.Client', 'tools/Fogell.Retention', 'tools/Fogell.Recovery']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    def git(*argv):
        return subprocess.check_output(['git', *argv], cwd=repo, timeout=30)
    if git('diff', 'HEAD', '--', *SOURCE).strip():
        raise ValueError('tracked release source differs from HEAD')
    args.output.mkdir(parents=True, exist_ok=False)
    commit = git('rev-parse', 'HEAD').decode().strip()
    archive = args.output / 'source.tar'
    archive.write_bytes(git('archive', 'HEAD', *SOURCE))
    build_tree = args.output / 'build'
    build_tree.mkdir()
    with tarfile.open(archive) as tar:
        tar.extractall(build_tree, filter='data')
    with (args.output / 'build.log').open('wb') as log:
        subprocess.run(['dotnet', 'restore', 'Fogell.slnx', '--locked-mode'], cwd=build_tree,
                       stdout=log, stderr=subprocess.STDOUT, check=True, timeout=600)
        subprocess.run(['dotnet', 'build', 'Fogell.slnx', '-c', 'Release', '--no-restore'], cwd=build_tree,
                       stdout=log, stderr=subprocess.STDOUT, check=True, timeout=600)
    if git('diff', 'HEAD', '--', *SOURCE).strip() or git('rev-parse', 'HEAD').decode().strip() != commit:
        raise ValueError('source changed while building')
    release = args.output / 'release'
    release.mkdir()
    with tarfile.open(archive) as tar:
        tar.extractall(release, filter='data')
    for project in BINARIES:
        shutil.copytree(build_tree / project / 'bin/Release/net10.0', release / project / 'bin/Release/net10.0')
    files = {str(p.relative_to(release)): hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted(release.rglob('*')) if p.is_file()}
    manifest = {'commit': commit, 'files': files}
    (release / 'release-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    package = args.output / 'release.tgz'
    with tarfile.open(package, 'w:gz') as tar:
        for child in sorted(release.iterdir()):
            tar.add(child, arcname=child.name)
    print(json.dumps({'commit': commit, 'archive': str(package),
                      'sha256': hashlib.sha256(package.read_bytes()).hexdigest(), 'files': len(files)}))


if __name__ == '__main__':
    main()
