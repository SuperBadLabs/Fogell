#!/usr/bin/env python3
"""Record final read-only health and protected identities after qualification."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import time
import urllib.request


def require(value, message):
    if not value:
        raise ValueError(message)


def command(argv):
    process = subprocess.run(argv, capture_output=True, timeout=15)
    require(process.returncode == 0, 'handoff observation failed: ' + Path(argv[0]).name)
    return process.stdout.decode().strip()


def main():
    require(os.uname().nodename == 'luigi', 'Luigi only')
    root = Path.home() / 'services/fogell'
    config = json.loads((root / 'deployment.json').read_text())
    qualified = (root / 'campaigns/qualification/receipt.json').read_bytes()
    q = json.loads(qualified)
    require(q['passed'] is True and len(q['runs']) == 60, 'qualification incomplete')
    require(q['declaration']['deployment']['deployment'] == config, 'qualification deployment differs')
    supervisor = json.loads((root / 'campaigns/qualification-supervisor.json').read_text())
    require(supervisor['passed'] is True and supervisor['exit_code'] == 0, 'qualification supervisor incomplete')
    result = {key: config[key] for key in ('source_commit', 'controller_container_id', 'postgres_container_id')}
    result.update(passed=False, phase='post_qualification',
                  qualification_receipt_sha256=hashlib.sha256(qualified).hexdigest(),
                  harness_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  services={}, effective_cgroups={})
    release = Path(config.get('release_root', root / 'release')).resolve()
    require(release.is_relative_to(root) and release != root, 'unowned release')
    manifest_bytes = (release / 'release-manifest.json').read_bytes()
    manifest = json.loads(manifest_bytes)
    require(manifest['commit'] == config['source_commit'], 'release commit differs')
    for name, expected in manifest['files'].items():
        path = release / name
        require(path.resolve().is_relative_to(release) and hashlib.sha256(path.read_bytes()).hexdigest() == expected,
                'release file differs')
    result['release_manifest_sha256'] = hashlib.sha256(manifest_bytes).hexdigest()
    for name, key, unit_key in [('controller', 'controller_container_id', 'service_unit'),
                                ('postgres', 'postgres_container_id', 'postgres_service_unit')]:
        identity = config[key]
        fields = command(['podman', 'inspect', '--format', '{{.Id}} {{.State.Running}} {{.State.Pid}}', identity]).split()
        require(fields[0] == identity and fields[1] == 'true' and int(fields[2]) > 0, 'owned container not running')
        unit = config[unit_key]
        require(unit == ('fogell.service' if name == 'controller' else 'fogell-postgres.service'), 'unit differs')
        require(identity in command(['systemctl', '--user', 'show', unit, '--property=ExecStart', '--value']), 'unit ownership changed')
        require(command(['systemctl', '--user', 'is-active', unit]) == 'active', 'service not active')
        require(command(['systemctl', '--user', 'is-enabled', unit]) == 'enabled', 'service not enabled')
        result['services'][name] = {'active': True, 'enabled': True}
        values = command(['podman', 'exec', identity, 'sh', '-c',
                          'cat /sys/fs/cgroup/memory.max /sys/fs/cgroup/pids.max /sys/fs/cgroup/cpu.max /sys/fs/cgroup/memory.swap.max']).splitlines()
        expected = ['8589934592', '512', '800000 100000', '0'] if name == 'controller' else ['2147483648', '256', '200000 100000', '0']
        require(values == expected, 'effective resource bounds changed')
        result['effective_cgroups'][name] = dict(zip(['memory.max', 'pids.max', 'cpu.max', 'memory.swap.max'], values))
        if name == 'controller':
            listeners = command(['ss', '-H', '-ltnp', 'sport = :46206'])
            require(len(listeners.splitlines()) == 1 and '127.0.0.1:46206' in listeners
                    and f'pid={fields[2]},' in listeners, 'API listener is not owned loopback')
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            raise ValueError('readiness redirected')
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.ProxyHandler({}))
    with opener.open('http://127.0.0.1:46206/health/ready', timeout=5) as response:
        require(response.status == 200, 'readiness failed')
        result['ready_status'] = response.status
    before = json.loads((root / 'protected-before.json').read_text())
    protected = []
    for row in before:
        template = '{"id":{{json .ID}},"started_at":{{json .State.StartedAt}},"running":{{json .State.Running}}}'
        observed = json.loads(command(['podman', 'inspect', '--format', template, row['id']]))
        require(observed == {'id': row['id'], 'started_at': row['started_at'], 'running': True},
                'protected container changed')
        protected.append(observed)
    require(len(protected) == 5, 'protected inventory differs')
    result['protected'] = protected
    size = count = 0
    deadline = time.monotonic() + 10
    for parent, directories, files in os.walk(root / 'state', followlinks=False):
        for name in directories + files:
            info = (Path(parent) / name).lstat()
            count += 1
            require(count <= 100000 and time.monotonic() < deadline, 'state inventory bound')
            if stat.S_ISREG(info.st_mode):
                size += info.st_size
    fs = os.statvfs(root)
    result.update(state_content_bytes=size, state_entries=count,
                  host_free_bytes=fs.f_bavail * fs.f_frsize, host_free_inodes=fs.f_favail)
    require(size < 2147483648 and result['host_free_bytes'] > 21474836480, 'storage guard exceeded')
    pg = config['postgres_container_id']
    result['database_bytes'] = int(command(['podman', 'exec', pg, 'psql', '-X', '-q', '-A', '-t', '-U', 'fogell', '-d', 'fogell',
                                          '-c', "SELECT pg_database_size(current_database())"]))
    result['postgres_volume_allocated_bytes'] = int(command(['podman', 'exec', pg, 'du', '-s', '-B1', '/var/lib/postgresql/data']).split()[0])
    result['user_linger_enabled'] = command(['loginctl', 'show-user', 'srikanth', '--property=Linger', '--value']) == 'yes'
    require(result['user_linger_enabled'], 'user linger disabled')
    result['host_reboot_tested'] = False
    result['recorded_at_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    result['passed'] = True
    output = root / 'campaigns/handoff.json'
    with output.open('x') as stream:
        json.dump(result, stream, indent=2)
        stream.write('\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
