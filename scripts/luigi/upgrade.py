#!/usr/bin/env python3
"""Replace only the recorded Luigi controller after campaigns have stopped.

Keeps the old container/release for rollback, reuses the owned database/state,
and restores the original unit on failure. No schema migration is performed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import time
import urllib.request
import uuid


def require(value, message):
    if not value:
        raise ValueError(message)


def write(path, text):
    temporary = path.with_name(path.name + '.new')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(text)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def command(argv, timeout=45):
    process = subprocess.run(argv, capture_output=True, timeout=timeout)
    require(process.returncode == 0, 'upgrade command failed: ' + Path(argv[0]).name)
    return process.stdout.decode().strip()


def state(cid):
    fields = command(['podman', 'inspect', '--format', '{{.Id}} {{.State.Running}} {{.State.Pid}}', cid]).split()
    require(len(fields) == 3 and fields[0] == cid, 'container identity changed')
    return {'id': fields[0], 'running': fields[1] == 'true', 'pid': int(fields[2])}


def healthy(cid):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    until = time.monotonic() + 45
    while time.monotonic() < until:
        observed = state(cid)
        listeners = command(['ss', '-H', '-ltnp', 'sport = :46206'])
        if observed['running'] and observed['pid'] > 0 and f"pid={observed['pid']}," in listeners:
            require('127.0.0.1:46206' in listeners, 'listener is not loopback')
            try:
                with opener.open('http://127.0.0.1:46206/health/ready', timeout=1) as response:
                    if response.status == 200:
                        return
            except OSError:
                pass
        time.sleep(.25)
    raise RuntimeError('owned controller readiness deadline')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-archive', type=Path, required=True)
    parser.add_argument('--sha256', required=True)
    args = parser.parse_args()
    root = Path.home() / 'services/fogell'
    require(os.uname().nodename == 'luigi', 'Luigi only')
    config = json.loads((root / 'deployment.json').read_text())
    old = config['controller_container_id']
    require(config['service_unit'] == 'fogell.service' and config['database'] == 'fogell'
            and config['state_root'] == str(root / 'state'), 'unexpected deployment')
    require(re.fullmatch('[a-f0-9]{64}', old) and re.fullmatch('[a-f0-9]{64}', config['image']), 'full identities required')
    require(old not in config['protected_container_ids'] and old != config['postgres_container_id'], 'unowned controller')
    for name in ('fogell-pilot-20260923.service', 'fogell-adversarial-20260923.service',
                 'fogell-adversarial-v2-20260923.service', 'fogell-adversarial-v3-20260923.service',
                 'fogell-qualification-20260923.service'):
        result = subprocess.run(['systemctl', '--user', 'is-active', name], capture_output=True, timeout=5)
        require(result.returncode in (3, 4) and result.stdout.strip() in (b'inactive', b'failed', b'unknown'),
                'campaign must finish before upgrade')
    unit = Path.home() / '.config/systemd/user/fogell.service'
    original_unit = unit.read_text()
    require(f'ExecStart=/usr/bin/podman start --attach {old}\n' in original_unit
            and f'ExecStop=/usr/bin/podman stop --time 20 {old}\n' in original_unit,
            'unit does not own recorded controller')
    require(state(old)['running'], 'original controller must start healthy')
    healthy(old)
    require(hashlib.sha256(args.release_archive.read_bytes()).hexdigest() == args.sha256, 'archive digest differs')
    fs = os.statvfs(root)
    require(fs.f_bavail * fs.f_frsize > 20 * 1024**3, 'free storage guard')
    identity = uuid.uuid4().hex
    release = root / 'releases' / identity
    release.mkdir(parents=True, mode=0o700)
    with tarfile.open(args.release_archive) as archive:
        members = archive.getmembers()
        require(len(members) < 10000 and sum(m.size for m in members) < 512 * 1024**2,
                'release exceeds archive bounds')
        require(all(m.isfile() or m.isdir() for m in members), 'release contains special file')
        archive.extractall(release, filter='data')
    manifest = json.loads((release / 'release-manifest.json').read_text())
    require(re.fullmatch('[a-f0-9]{40}', manifest['commit']), 'release commit required')
    for name, expected in manifest['files'].items():
        file = release / name
        require(file.resolve().is_relative_to(release) and file.is_file(), 'unsafe release file')
        require(hashlib.sha256(file.read_bytes()).hexdigest() == expected, 'release hash differs')
    # Controller startup applies embedded migrations. Restrict this rollback
    # procedure to a code-only release with the exact existing migration set.
    previous_release = Path(config.get('release_root', root / 'release')).resolve()
    require(previous_release.is_relative_to(root) and previous_release != root, 'unowned active release')
    def migrations(base):
        return {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted((base / 'src/Fogell.Store/migrations').glob('*.sql'))}
    previous_migrations = migrations(previous_release)
    require(previous_migrations and migrations(release) == previous_migrations,
            'schema changes require a separate migration and rollback plan')
    receipt = {'passed': False, 'old_deployment': config, 'new_release': str(release),
               'new_source_commit': manifest['commit'], 'archive_sha256': args.sha256,
               'cleanup_errors': [], 'rollback_succeeded': None}
    output = root / 'campaigns' / ('upgrade-' + identity + '.json')
    def protected():
        template = '{"id":{{json .ID}},"started_at":{{json .State.StartedAt}},"running":{{json .State.Running}}}'
        return [json.loads(command(['podman', 'inspect', '--format', template, cid]))
                for cid in sorted(config['protected_container_ids'])]
    before = protected()
    receipt['protected_before'] = before
    changed = False
    new = None
    try:
        new = command(['podman', 'create', '--name', 'fogell-controller-' + identity,
                       '--label', 'fogell.owner=luigi-pilot-20260923', '--network', 'host', '--read-only',
                       '--cpus', '8', '--memory', '8g', '--memory-swap', '8g', '--pids-limit', '512',
                       '--log-opt', 'max-size=16mb', '--tmpfs', '/tmp:rw,size=256m,mode=1777',
                       '--env-file', str(root / 'private/controller.env'),
                       '-v', str(release) + ':/app:ro', '-v', str(root / 'state') + ':/data:rw',
                       '-v', str(root / 'private/token') + ':/run/fogell/token:ro',
                       '-v', str(root / 'packages') + ':/tmp/fogell-pilot-luigi/packages:ro',
                       '-v', str(root / 'campaigns') + ':/campaign:rw', config['image'],
                       '/app/src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host'])
        require(re.fullmatch('[a-f0-9]{64}', new) and new not in {old, config['postgres_container_id']}
                and new not in config['protected_container_ids'], 'new controller identity invalid')
        receipt['new_controller_container_id'] = new
        write(output, json.dumps(receipt, indent=2) + '\n')
        changed = True
        command(['systemctl', '--user', 'stop', 'fogell.service'])
        require(state(old) == {'id': old, 'running': False, 'pid': 0}, 'original controller not extinct')
        write(unit, original_unit.replace(old, new))
        command(['systemctl', '--user', 'daemon-reload'])
        command(['systemctl', '--user', 'start', 'fogell.service'])
        healthy(new)
        receipt['protected_after'] = protected()
        require(receipt['protected_after'] == before, 'protected services changed')
        updated = dict(config, controller_container_id=new, release_root=str(release),
                       source_commit=manifest['commit'], release_archive_sha256=args.sha256)
        write(root / 'deployment.json', json.dumps(updated, indent=2) + '\n')
        receipt['new_deployment'] = updated
        receipt['passed'] = True
    except BaseException as error:
        receipt['error'] = type(error).__name__ + ': ' + str(error)
        if changed:
            try:
                command(['systemctl', '--user', 'stop', 'fogell.service'])
                write(unit, original_unit)
                command(['systemctl', '--user', 'daemon-reload'])
                command(['systemctl', '--user', 'start', 'fogell.service'])
                healthy(old)
                receipt['rollback_succeeded'] = True
            except BaseException as cleanup:
                receipt['cleanup_errors'].append(type(cleanup).__name__ + ': ' + str(cleanup))
                receipt['rollback_succeeded'] = False
        raise
    finally:
        # Keep both exact identities for inspection; never prune or erase evidence.
        write(output, json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({'passed': True, 'receipt': str(output), 'controller_container_id': new}))


if __name__ == '__main__':
    main()
