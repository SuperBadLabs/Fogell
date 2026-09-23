#!/usr/bin/env python3
"""Owned Luigi paired-recovery drill. Original service is restored in finally.

Run only after the pilot/fault campaign is quiescent. Backups and restored state
remain private. This script never drops databases or removes the persistent
controller. --self-test checks pure helpers without contacting a deployment.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import tempfile
import time
import urllib.request
import uuid

PROTECTED = ['ag1', 'ctrl', 'jenkins-bench', 'jenkins-lab', 'mcloving-faceoff2']
MAX_BYTES = 2 * 1024**3
MIN_FREE = 20 * 1024**3


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write(path, value):
    payload = json.dumps(value, indent=2) + '\n'
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(payload)


def target_environment(source, database, expected_port=None):
    require(re.fullmatch(r'fogell_luigi_restore_[a-z0-9_]+', database), 'invalid target database')
    lines, found = [], set()
    for line in source.splitlines():
        key, separator, value = line.partition('=')
        if key in {'FOGELL_DATABASE_URL', 'FOGELL_MAINTENANCE_DATABASE_URL'}:
            require(separator and key not in found, 'ambiguous private connection configuration')
            parts, fields = [], {}
            for part in value.rstrip(';').split(';'):
                name, equal, setting = part.partition('=')
                normalized = name.casefold()
                require(equal and normalized in {'host', 'port', 'username', 'password', 'database'} and
                        normalized not in fields and not any(c in setting for c in '\r\n\"\''),
                        'connection is not the unambiguous generated Luigi format')
                fields[normalized] = setting
                parts.append(name + '=' + (database if normalized == 'database' else setting))
            require(fields.get('database') == 'fogell' and fields.get('host') == '127.0.0.1',
                    'source connection must identify owned loopback fogell database')
            if expected_port is not None:
                role = 'fogell_runtime' if key == 'FOGELL_DATABASE_URL' else 'fogell'
                require(set(fields) == {'host', 'port', 'username', 'password', 'database'} and
                        fields['port'] == str(expected_port) and fields['username'] == role,
                        'private connection endpoint or role differs from owned deployment')
            value = ';'.join(parts)
            found.add(key)
            line = key + '=' + value
        lines.append(line)
    require(len(found) == 2, 'missing private connection configuration')
    return '\n'.join(lines) + '\n'


def guard(root, paths):
    count = size = 0
    for base in paths:
        require(base.is_dir() and not base.is_symlink(), 'guard root must be real directory')
        for parent, directories, files in os.walk(base, followlinks=False):
            for name in directories + files:
                item = Path(parent) / name
                info = item.lstat()
                require(not stat.S_ISLNK(info.st_mode), 'guard refuses symbolic links')
                count += 1
                require(count <= 100000, 'guard inventory exceeds bound')
                if stat.S_ISREG(info.st_mode):
                    size += info.st_size
                    require(size < MAX_BYTES, 'guard content bytes exceed 2GiB')
    fs = os.statvfs(root)
    require(fs.f_bavail * fs.f_frsize > MIN_FREE and fs.f_favail > 10000, 'host free storage reserve breached')
    return {'content_bytes': size, 'entries': count, 'free_bytes': fs.f_bavail * fs.f_frsize,
            'free_inodes': fs.f_favail, 'is_hard_quota': False}


def cleanup_endpoints(state, journal=None):
    require(state.is_dir() and not state.is_symlink(), 'cleanup root must be a real directory')
    plan = []
    scanned = 0
    for parent, directories, files in os.walk(state, followlinks=False):
        for name in directories + files:
            scanned += 1
            require(scanned <= 100000, 'cleanup inventory exceeds bound')
            path = Path(parent) / name
            info = path.lstat()
            if stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode):
                continue
            relative = path.relative_to(state)
            match = re.fullmatch(r'(?:clr-debug-pipe-(\d+)-\d+-(?:in|out)|dotnet-diagnostic-(\d+)-\d+-socket)', name)
            require(not stat.S_ISLNK(info.st_mode) and match and
                    (stat.S_ISFIFO(info.st_mode) or stat.S_ISSOCK(info.st_mode)) and
                    any(part in {'tmp', '@tmp'} or part.endswith('@tmp') for part in relative.parts[:-1]),
                    'unrecognized state special file; keep quiesced for inspection')
            # The PID in a container endpoint name is namespace-local. Caller
            # must first prove the entire original PID namespace extinct.
            plan.append({'path': str(relative), 'mode': info.st_mode, 'uid': info.st_uid,
                         'device': info.st_dev, 'inode': info.st_ino,
                         'namespace_local_pid': int(match[1] or match[2]), 'namespace_extinct': True})
    # Validate the entire plan before removing even one endpoint. The optional
    # private journal retains intent/completion if an unlink or later step fails.
    progress = {'planned': plan, 'completed': [], 'next': None}
    if journal is not None:
        require(not journal.exists(), 'cleanup journal already exists')
    def persist():
        if journal is None:
            return
        temporary = journal.with_name(journal.name + '.' + uuid.uuid4().hex)
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as stream:
            json.dump(progress, stream)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(journal)
        directory_fd = os.open(journal.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    persist()
    for item in plan:
        path = state / item['path']
        current = path.lstat()
        require((current.st_dev, current.st_ino, current.st_mode) == (item['device'], item['inode'], item['mode']),
                'cleanup endpoint identity changed')
        progress['next'] = item['path']
        persist()
        path.unlink()
        progress['completed'].append(item['path'])
        progress['next'] = None
        persist()
    return plan


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path.home() / 'services/fogell')
    parser.add_argument('--target-database', default='fogell_luigi_restore_20260923')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        sample = 'FOGELL_DATABASE_URL=Host=127.0.0.1;Password=secret;Database=fogell\nFOGELL_MAINTENANCE_DATABASE_URL=Database=fogell;Host=127.0.0.1\nX=y\n'
        result = target_environment(sample, 'fogell_luigi_restore_test')
        require(result.count('Database=fogell_luigi_restore_test') == 2 and 'Password=secret' in result, 'environment rewrite')
        for bad in ['fogell', 'postgres', 'fogell_luigi_restore_a;Database=postgres']:
            try:
                target_environment(sample, bad)
            except ValueError:
                pass
            else:
                raise AssertionError('unsafe target accepted')
        print(json.dumps({'passed': True, 'synthetic_controls': True, 'live_recovery': False}))
        return

    root = args.root.resolve()
    require(root == Path.home() / 'services/fogell' and os.uname().nodename == 'luigi', 'requires owned Luigi deployment root')
    for relative in ['private', 'backups', 'state', 'campaigns']:
        require((root / relative).is_dir() and not (root / relative).is_symlink(), 'owned directory missing or linked')
    require(re.fullmatch(r'fogell_luigi_restore_[a-z0-9_]+', args.target_database), 'invalid target name')
    config = json.loads((root / 'deployment.json').read_text())
    require(config['database'] == 'fogell' and config['service_unit'] == 'fogell.service' and
            config['url'] == 'http://127.0.0.1:46206' and config['state_root'] == str(root / 'state'), 'unexpected deployment')
    for key in ['controller_container_id', 'postgres_container_id', 'image']:
        require(re.fullmatch('[a-f0-9]{64}', config[key]), 'deployment lacks full immutable identity')
    ctl, pg = config['controller_container_id'], config['postgres_container_id']
    require(ctl != pg, 'controller and postgres identity overlap')
    env_source = root / 'private/controller.env'
    require(env_source.is_file() and not env_source.is_symlink() and env_source.stat().st_mode & 0o077 == 0,
            'private environment must be an owner-only regular file')
    require(type(config['postgres_port']) is int and 1 <= config['postgres_port'] <= 65535, 'invalid owned PostgreSQL port')
    private_source = env_source.read_text()
    target_env_text = target_environment(private_source, args.target_database, config['postgres_port'])
    helper = Path(__file__).with_name('recovery-stale.fsx').resolve()
    release = Path(config.get('release_root', root / 'release')).resolve()
    require(release.is_relative_to(root) and release.is_dir() and release != root, 'release root is outside owned deployment')
    spec = importlib.util.spec_from_file_location('paired_recovery', release / 'scripts/prove-paired-recovery.py')
    recovery = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(recovery)
    require(hasattr(recovery, 'logical_inventory'), 'release lacks complete-row recovery checker')
    output = root / 'backups' / args.target_database
    output.mkdir(mode=0o700)  # Refuse reuse even after an earlier failed drill.
    restored = output / 'restored-state'
    campaign_relative = 'recovery-' + args.target_database
    campaign = root / 'campaigns' / campaign_relative
    require(not campaign.exists(), 'recovery campaign already exists')
    campaign.mkdir(mode=0o700)
    private_env = output / 'controller.env'
    fd = os.open(private_env, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(target_env_text)
    receipt = {'schema_version': 1, 'passed': False, 'source_database': 'fogell', 'target_database': args.target_database,
               'controller_container_id': ctl, 'postgres_container_id': pg, 'image': config['image'],
               'source_commit': config['source_commit'], 'rto_seconds_target': 120, 'rpo_lost_records_target': 0,
               'adapter_sha256': sha(Path(__file__).read_bytes()), 'stale_helper_sha256': sha(helper.read_bytes()),
               'checker_sha256': sha((release / 'scripts/prove-paired-recovery.py').read_bytes()),
               'temporary_containers': [], 'cleanup_errors': []}
    stopped = False
    quiesce_started = None
    temporary = None
    deadline = None
    child_ids = []
    creation_intents = []

    def journal(event):
        fd = os.open(output / 'container-intents.ndjson', os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
        with os.fdopen(fd, 'w') as stream:
            stream.write(json.dumps(event) + '\n')
            stream.flush()
            os.fsync(stream.fileno())

    def command(argv, data=None, timeout=30, maximum=256 * 1024**2):
        if deadline is not None:
            timeout = min(timeout, max(0.1, deadline - time.monotonic()))
            require(time.monotonic() < deadline, 'recovery exceeded 120 second target')
        # Spool output rather than accepting unbounded subprocess pipes in RAM.
        require(data is None or len(data) <= maximum, 'command input exceeds bound')
        with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr, tempfile.TemporaryFile() as stdin:
            if data is not None:
                stdin.write(data)
                stdin.seek(0)
            process = subprocess.Popen(argv, stdin=stdin, stdout=stdout, stderr=stderr)
            command_end = time.monotonic() + timeout
            try:
                while process.poll() is None:
                    require(os.fstat(stdout.fileno()).st_size <= maximum and os.fstat(stderr.fileno()).st_size <= maximum,
                            'command output exceeds bound')
                    require(time.monotonic() < command_end, 'bounded recovery command timed out: ' + Path(argv[0]).name)
                    time.sleep(.05)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
            require(process.returncode == 0, 'recovery command failed: ' + Path(argv[0]).name)
            require(stdout.tell() <= maximum and stderr.tell() <= maximum, 'command output exceeds bound')
            stdout.seek(0)
            return stdout.read()

    def inspect(identity):
        row = json.loads(command(['podman', 'inspect', identity]))[0]
        require(row['Id'] == identity, 'container identity changed')
        return row

    def protected():
        rows = json.loads(command(['podman', 'inspect'] + PROTECTED))
        result = sorted([{'name': r['Name'].lstrip('/'), 'id': r['Id'],
                          'started_at': r['State']['StartedAt'], 'status': r['State']['Status']} for r in rows], key=lambda r: r['name'])
        require([r['name'] for r in result] == PROTECTED and all(r['status'] == 'running' for r in result), 'protected services incomplete')
        require(ctl not in {r['id'] for r in result} and pg not in {r['id'] for r in result}, 'owned identity is protected')
        return result

    def sql(database, statement):
        return command(['podman', 'exec', '-i', pg, 'psql', '-X', '-q', '-A', '-t', '-U', 'fogell', '-d', database,
                        '-v', 'ON_ERROR_STOP=1'], statement.encode()).decode().strip()

    def logical(database):
        result = recovery.logical_inventory(sql, database)
        schema = command(['podman', 'exec', pg, 'pg_dump', '-U', 'fogell', '-d', database, '--schema-only'])
        result['schema_sha256'] = sha(b'\n'.join(line for line in schema.splitlines() if not line.startswith((b'\\restrict ', b'\\unrestrict '))))
        return result

    def create(argv):
        nonce = uuid.uuid4().hex
        intent = {'name': 'fogell-recovery-' + nonce, 'label': nonce,
                  'cidfile': str(output / ('container-' + nonce + '.cid')), 'id': None, 'removed': False}
        creation_intents.append(intent)
        journal({'event': 'create_intent', **intent})
        identity = command(['podman', 'create', '--name', intent['name'], '--label', 'fogell.recovery=' + nonce,
                            '--cidfile', intent['cidfile']] + argv).decode().strip()
        require(re.fullmatch('[a-f0-9]{64}', identity), 'create did not return full identity')
        intent['id'] = identity
        child_ids.append(identity)
        receipt['temporary_containers'].append(identity)
        journal({'event': 'created', **intent})
        return identity

    def recover_creation(intent):
        if intent['id'] is not None or intent['removed']:
            return
        cidfile = Path(intent['cidfile'])
        identity = None
        if cidfile.exists():
            require(cidfile.is_file() and not cidfile.is_symlink() and cidfile.stat().st_size <= 128,
                    'invalid owned creation CID file')
            candidate = cidfile.read_text().strip()
            if re.fullmatch('[a-f0-9]{64}', candidate):
                identity = candidate
        if identity is not None:
            row = inspect(identity)
        else:
            exists = subprocess.run(['podman', 'container', 'exists', intent['name']], stdout=subprocess.DEVNULL,
                                    stderr=subprocess.DEVNULL, timeout=5)
            require(exists.returncode in {0, 1}, 'cannot establish interrupted creation state')
            if exists.returncode == 1:
                intent['removed'] = True
                journal({'event': 'creation_absent', **intent})
                return
            row = json.loads(command(['podman', 'inspect', intent['name']]))[0]
            identity = row['Id']
        require(re.fullmatch('[a-f0-9]{64}', identity) and identity not in {ctl, pg} and
                row['Name'].lstrip('/') == intent['name'] and
                row['Config']['Labels'].get('fogell.recovery') == intent['label'] and
                row['Image'].removeprefix('sha256:') == config['image'], 'interrupted creation ownership mismatch')
        intent['id'] = identity
        child_ids.append(identity)
        receipt['temporary_containers'].append(identity)
        journal({'event': 'creation_recovered', **intent})

    def remove(identity):
        require(identity in child_ids and identity not in {ctl, pg}, 'unowned cleanup target')
        exists = subprocess.run(['podman', 'container', 'exists', identity], stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL, timeout=5)
        require(exists.returncode in {0, 1}, 'cannot establish owned cleanup state')
        if exists.returncode == 0:
            inspect(identity)
            command(['podman', 'rm', '--force', identity])
        child_ids.remove(identity)
        intent = next(item for item in creation_intents if item['id'] == identity)
        intent['removed'] = True
        journal({'event': 'removed', **intent})

    def tool(envfile, arguments):
        identity = create(['--network', 'host', '--read-only', '--cpus', '2', '--memory', '1g', '--memory-swap', '1g',
                           '--pids-limit', '128', '--tmpfs', '/tmp:rw,size=256m', '--env', 'DOTNET_CLI_HOME=/tmp',
                           '--env-file', str(envfile), '-v', str(release) + ':/app:ro',
                           '-v', str(helper) + ':/proof/recovery-stale.fsx:ro',
                           '-v', str(output) + ':/proof-output:rw', config['image']] + arguments)
        try:
            return command(['podman', 'start', '--attach', identity], timeout=45)
        finally:
            # Outer finally removes any tool still alive after a deadline.
            if deadline is None or time.monotonic() < deadline:
                remove(identity)

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *redirect_args, **redirect_kwargs):
            raise ValueError('recovery endpoint redirected')
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.ProxyHandler({}))
    def healthy(identity, timeout=30):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            running = inspect(identity)['State']
            require(running['Running'] and running['Pid'] > 0, 'intended controller is not running')
            listeners = command(['ss', '-H', '-ltnp', 'sport = :46206']).decode()
            if not listeners.strip():
                time.sleep(.2)
                continue
            require('127.0.0.1:46206' in listeners and f"pid={running['Pid']}," in listeners and
                    len(listeners.splitlines()) == 1, 'listener is not exclusively owned loopback controller')
            try:
                with opener.open(config['url'] + '/health/ready', timeout=1) as response:
                    if response.status == 200:
                        return
            except Exception:
                pass
            time.sleep(.2)
        raise RuntimeError('controller did not become ready')

    def interrupted(signum, frame):
        raise InterruptedError('recovery interrupted')
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        receipt['protected_before'] = protected()
        saved_protected = json.loads((root / 'protected-before.json').read_text())
        require({r['name']: (r['id'], r['started_at']) for r in saved_protected} ==
                {r['name']: (r['id'], r['started_at']) for r in receipt['protected_before']}, 'protected services changed since deployment')
        original = inspect(ctl)
        require(original['State']['Running'] and original['Image'].removeprefix('sha256:') == config['image'], 'original controller identity differs')
        require(original['HostConfig']['ReadonlyRootfs'], 'original controller is not read-only')
        mounts = {m['Destination']: m for m in original['Mounts']}
        for destination, source, writable in [('/app', release, False), ('/data', root / 'state', True),
                                               ('/run/fogell/token', root / 'private/token', False)]:
            require(destination in mounts and mounts[destination]['Source'] == str(source) and
                    mounts[destination]['RW'] is writable, 'original mount ownership differs')
        pg_info = inspect(pg)
        require(pg_info['State']['Running'] and pg_info['Image'].removeprefix('sha256:') ==
                config['postgres_image'].removeprefix('sha256:'), 'owned PostgreSQL identity differs')
        require(command(['podman', 'port', pg, '5432/tcp']).decode().strip() == '127.0.0.1:' + str(config['postgres_port']),
                'private connection port is not owned PostgreSQL listener')
        private_fields = dict(line.split('=', 1) for line in private_source.splitlines() if line)
        container_fields = dict(line.split('=', 1) for line in original['Config']['Env'])
        for key in ['FOGELL_DATABASE_URL', 'FOGELL_MAINTENANCE_DATABASE_URL']:
            require(container_fields.get(key) == private_fields[key], 'controller environment differs from private connection file')
        unit = command(['systemctl', '--user', 'show', config['service_unit'], '--property=ExecStart', '--value']).decode()
        require(ctl in unit and 'podman start' in unit, 'service does not own recorded controller')
        campaign_units = json.loads(command(['systemctl', '--user', 'list-units', '--all', '--type=service',
                                            '--output=json', 'fogell-pilot-*', 'fogell-adversarial-*']))
        require(all(row['active'] in {'inactive', 'failed'} for row in campaign_units), 'campaign units must finish before recovery')
        receipt['campaign_units'] = campaign_units
        healthy(ctl)
        require(sql('postgres', "SELECT count(*) FROM pg_database WHERE datname='" + args.target_database + "'") == '0', 'target database already exists')
        manifest = json.loads((release / 'release-manifest.json').read_text())
        require(manifest['commit'] == config['source_commit'], 'deployed source commit differs')
        for relative, digest in manifest['files'].items():
            require(sha((release / relative).read_bytes()) == digest, 'deployed release file differs')
        receipt['release_manifest_sha256'] = sha((release / 'release-manifest.json').read_bytes())
        pilot_path = root / 'campaigns/pilot/receipt.json'
        pilot = json.loads(pilot_path.read_text())
        require(pilot['passed'] is True and len(pilot['runs']) == 200, '100-loop pilot must pass first')
        latest = pilot['runs'][-1]
        require(latest['kind'] == 'corrected' and latest['passed'], 'last pilot run is not corrected')
        workload = {'organization': config['organization'], 'project': config['project'],
                    'build': latest['admission']['build_id'], 'attempt': latest['admission']['attempt_id'],
                    'junit_sha256': latest['junit_sha256']}
        identifiers = {k: str(uuid.UUID(workload[k])) for k in ['organization', 'project', 'build', 'attempt']}
        artifact = Path('workspaces') / uuid.UUID(identifiers['organization']).hex / '_artifact-snapshots' / uuid.UUID(identifiers['attempt']).hex / 'reports/domain.xml'
        require(sha((root / 'state' / artifact).read_bytes()) == workload['junit_sha256'], 'last corrected artifact expired or differs')
        write(output / 'workload.json', workload)
        receipt['pilot_receipt_sha256'] = sha(pilot_path.read_bytes())
        current_declaration = json.loads((root / 'campaigns/deployment.json').read_text())
        require(current_declaration['deployment']['controller_container_id'] == ctl and
                current_declaration['deployment']['source_commit'] == config['source_commit'],
                'refresh campaign deployment declaration after controller upgrade')
        observed_tool = json.loads(command(['podman', 'exec', ctl, 'dotnet',
                                           '/app/tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll',
                                           'tool-identity', '--run-host',
                                           '/app/tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host']))
        require(observed_tool['run_host_sha256'] == current_declaration['run_host']['run_host_sha256'],
                'current deployed tool differs from refreshed declaration')
        smoke = json.loads((root / 'campaigns/smoke-command.json').read_text())
        require(smoke[:5] == ['podman', 'exec', ctl, 'python3', '/app/scripts/prove-self-hosted-pilot.py'],
                'refresh owned smoke command after controller upgrade')
        require(smoke.count('--tool-sha256') == 1 and smoke[smoke.index('--tool-sha256') + 1] == observed_tool['run_host_sha256'],
                'smoke command tool pin differs from deployed runner')
        receipt['tool_sha256'] = current_declaration['run_host']['run_host_sha256']
        receipt['original_workload_tool_sha256'] = pilot['declaration']['tool_sha256']
        receipt['workload'] = workload
        receipt['guard_before'] = guard(root, [root / 'state', root / 'backups'])
        pid = original['State']['Pid']
        namespace = os.readlink(f'/proc/{pid}/ns/pid')
        quiesce_started = time.monotonic()
        stopped = True
        command(['systemctl', '--user', 'stop', config['service_unit']], timeout=45)
        require(not inspect(ctl)['State']['Running'] and inspect(ctl)['State']['Pid'] == 0, 'original container did not stop')
        for process in Path('/proc').iterdir():
            if process.name.isdigit():
                try:
                    require(os.readlink(process / 'ns/pid') != namespace, 'original PID namespace still has a process')
                except (FileNotFoundError, PermissionError, ProcessLookupError):
                    pass
        receipt['namespace_extinct'] = namespace
        receipt['volatile_cleanup'] = cleanup_endpoints(root / 'state', output / 'volatile-cleanup.json')
        tool(env_source, ['dotnet', 'fsi', '--exec', '/proof/recovery-stale.fsx', 'seed', '/proof-output/stale-authority.json'])
        receipt['source_seed_authority'] = json.loads((output / 'stale-authority.json').read_text())
        require(sql('fogell', "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND backend_type='client backend'") == '0', 'source database has active clients')
        belongs = sql('fogell', f"SELECT count(*) FROM builds b JOIN nodes n ON n.organization_id=b.organization_id AND n.build_id=b.id JOIN attempts a ON a.organization_id=n.organization_id AND a.node_id=n.id WHERE b.organization_id='{identifiers['organization']}' AND b.project_id='{identifiers['project']}' AND b.id='{identifiers['build']}' AND a.id='{identifiers['attempt']}' AND a.state='terminal' AND NOT EXISTS(SELECT 1 FROM build_retention r WHERE r.organization_id=b.organization_id AND r.build_id=b.id)")
        require(belongs == '1', 'backup workload not retained terminal attempt')
        source_inventory = recovery.inventory(root / 'state')
        require(sum(v['size'] for v in source_inventory.values()) * 4 < MAX_BYTES, 'backup copies exceed guard budget')
        archive = output / 'database.custom'
        archive.write_bytes(command(['podman', 'exec', pg, 'pg_dump', '-U', 'fogell', '-d', 'fogell', '--format=custom'], timeout=60))
        require(archive.stat().st_size > 0, 'empty database archive')
        backup = output / 'state'
        shutil.copytree(root / 'state', backup)
        require(recovery.inventory(root / 'state') == source_inventory, 'source state changed during backup')
        pair = {'schema_version': 1, 'database_archive_sha256': sha(archive.read_bytes()), 'state_inventory': source_inventory,
                'database_inventory': logical('fogell'), 'source_commit': config['source_commit'],
                'release_manifest_sha256': receipt['release_manifest_sha256'], 'tool_sha256': receipt['tool_sha256']}
        write(output / 'pair-manifest.json', pair)
        recovery.verify_pair(pair, archive, backup)
        receipt['controls'] = recovery.controls(pair, archive, backup, output)
        receipt['guard_backup'] = guard(root, [root / 'state', root / 'backups'])
        origin = time.monotonic()
        deadline = origin + 120
        sql('postgres', 'CREATE DATABASE ' + args.target_database)
        command(['podman', 'exec', '-i', pg, 'pg_restore', '-U', 'fogell', '-d', args.target_database,
                 '--single-transaction', '--exit-on-error'], archive.read_bytes(), timeout=60)
        shutil.copytree(backup, restored)
        require(logical(args.target_database) == pair['database_inventory'], 'complete restored database differs')
        require(recovery.inventory(restored) == source_inventory, 'restored state differs')
        receipt['activation'] = json.loads(tool(private_env, ['dotnet', '/app/tools/Fogell.Recovery/bin/Release/net10.0/Fogell.Recovery.dll', 'activate-restore', '--writers-quiesced']))
        probe = tool(private_env, ['dotnet', 'fsi', '--exec', '/proof/recovery-stale.fsx', 'check', '/proof-output/stale-authority.json'])
        receipt['stale_authority'] = json.loads(probe.decode().splitlines()[-1])
        require(receipt['stale_authority']['passed'], 'stale authority accepted')
        temporary = create(['--network', 'host', '--read-only', '--cpus', '8', '--memory', '8g', '--memory-swap', '8g',
                            '--pids-limit', '512', '--log-opt', 'max-size=16mb', '--tmpfs', '/tmp:rw,size=256m,mode=1777',
                            '--env-file', str(private_env), '-v', str(release) + ':/app:ro', '-v', str(restored) + ':/data:rw',
                            '-v', str(root / 'private/token') + ':/run/fogell/token:ro',
                            '-v', str(root / 'packages') + ':/tmp/fogell-pilot-luigi/packages:ro',
                            '-v', str(root / 'campaigns') + ':/campaign:rw', config['image'],
                            '/app/src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host'])
        receipt['restored_controller_id'] = temporary
        command(['podman', 'start', temporary])
        healthy(temporary)
        token = (root / 'private/token').read_bytes()
        require(0 < len(token) <= 4096, 'invalid token file')
        url = config['url'] + f"/api/v1/organizations/{identifiers['organization']}/projects/{identifiers['project']}/builds/{identifiers['build']}/attempts/{identifiers['attempt']}/artifacts/reports/domain.xml"
        request = urllib.request.Request(url, headers={'Authorization': 'Bearer ' + token.decode().rstrip('\r\n')})
        with opener.open(request, timeout=5) as response:
            data = response.read(2 * 1024**2 + 1)
            require(response.status == 200 and len(data) <= 2 * 1024**2 and sha(data) == workload['junit_sha256'], 'restored API artifact differs')
        receipt['previous_artifact_sha256'] = sha(data)
        declaration = current_declaration
        declaration['recovery_target'] = {'database': args.target_database, 'state_root': str(restored), 'controller_container_id': temporary}
        write(campaign / 'deployment.json', declaration)
        smoke[2] = temporary
        for flag, value in [('--loops', '1'), ('--work', '/campaign/' + campaign_relative + '/work'),
                            ('--output', '/campaign/' + campaign_relative + '/control'),
                            ('--deployment-declaration', '/campaign/' + campaign_relative + '/deployment.json')]:
            require(smoke.count(flag) == 1, 'ambiguous smoke option')
            smoke[smoke.index(flag) + 1] = value
        write(campaign / 'command.json', smoke)
        command(smoke, timeout=120)
        control_path = campaign / 'control/receipt.json'
        control = json.loads(control_path.read_text())
        require(control['passed'] is True and len(control['runs']) == 2, 'restored correction pair failed')
        for run in control['runs']:
            build = str(uuid.UUID(run['admission']['build_id']))
            expected = 'unstable' if run['kind'] == 'failed' else 'success'
            require(sql(args.target_database, "SELECT status FROM builds WHERE id='" + build + "'") == expected, 'new control used wrong database')
        pilot_spec = importlib.util.spec_from_file_location('recovery_pilot_verify', release / 'scripts/prove-self-hosted-pilot.py')
        pilot_checker = importlib.util.module_from_spec(pilot_spec)
        pilot_spec.loader.exec_module(pilot_checker)
        pilot_checker.verify_artifacts(control_path, control)
        for run in control['runs']:
            pilot_checker.validate_run(run, 41)
        receipt['control_receipt_sha256'] = sha(control_path.read_bytes())
        receipt['control_receipt_path'] = str(control_path)
        receipt['new_builds'] = [r['admission']['build_id'] for r in control['runs']]
        receipt['recovery_elapsed_seconds'] = time.monotonic() - origin
        require(receipt['recovery_elapsed_seconds'] <= 120, 'recovery exceeded RTO')
        receipt['lost_backed_up_rows'] = receipt['lost_backed_up_files'] = 0
        receipt['passed'] = True
    except BaseException as error:
        # Do not include subprocess stderr, connection strings or arbitrary input.
        receipt['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        deadline = None
        for intent in creation_intents:
            try:
                recover_creation(intent)
            except Exception as error:
                receipt['cleanup_errors'].append({'action': 'recover_creation', 'name': intent['name'],
                                                   'error_type': type(error).__name__})
        for identity in list(reversed(child_ids)):
            try:
                remove(identity)
            except Exception as error:
                receipt['cleanup_errors'].append({'identity': identity, 'error_type': type(error).__name__})
        if receipt.get('source_seed_authority'):
            receipt['source_seed_cleanup'] = {'passed': False, 'state': 'pending_original_lease',
                                              'reason': 'restore_proof_incomplete'}
        if receipt['passed'] and not child_ids and not receipt['cleanup_errors']:
            # This happens only after the restored stale-writer rejection and
            # full correction pair succeeded. It cannot weaken the backup probe.
            try:
                result = tool(env_source, ['dotnet', 'fsi', '--exec', '/proof/recovery-stale.fsx',
                                          'cleanup-source', '/proof-output/stale-authority.json'])
                receipt['source_seed_cleanup'] = json.loads(result.decode().splitlines()[-1])
                require(receipt['source_seed_cleanup']['passed'], 'source probe reconciliation refused')
            except Exception as error:
                receipt['cleanup_errors'].append({'action': 'source_seed_reconciliation', 'error_type': type(error).__name__})
            # A timed-out cleanup helper is still an owned tool; finish exact-ID
            # cleanup before restarting the original controller.
            for intent in creation_intents:
                try:
                    recover_creation(intent)
                except Exception as error:
                    receipt['cleanup_errors'].append({'action': 'recover_cleanup_tool', 'error_type': type(error).__name__})
            for identity in list(reversed(child_ids)):
                try:
                    remove(identity)
                except Exception as error:
                    receipt['cleanup_errors'].append({'identity': identity, 'error_type': type(error).__name__})
        if stopped:
            try:
                require(not child_ids and all(i['removed'] for i in creation_intents),
                        'temporary container authority uncertain; refuse competing controller')
                require(inspect(ctl)['Id'] == ctl, 'original controller identity changed')
                command(['systemctl', '--user', 'start', config['service_unit']], timeout=45)
                healthy(ctl, timeout=45)
                receipt['original_service_restored'] = True
                receipt['original_service_downtime_seconds'] = time.monotonic() - quiesce_started
            except Exception as error:
                receipt['cleanup_errors'].append({'action': 'restore_original', 'error_type': type(error).__name__})
        try:
            receipt['protected_after'] = protected()
            require(receipt['protected_after'] == receipt.get('protected_before'), 'protected services changed')
            receipt['guard_final'] = guard(root, [root / 'state', root / 'backups'])
        except Exception as error:
            receipt['cleanup_errors'].append({'action': 'final_verification', 'error_type': type(error).__name__})
        if receipt['cleanup_errors'] or not receipt.get('original_service_restored'):
            receipt['passed'] = False
        write(output / 'receipt.json', receipt)
    print(json.dumps({'passed': receipt['passed'], 'receipt': str(output / 'receipt.json'),
                      'original_service_restored': receipt.get('original_service_restored', False)}))
    if not receipt['passed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
