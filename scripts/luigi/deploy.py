#!/usr/bin/env python3
"""First installation only. Run on Luigi with verified local release archives.

Creates a private, persistent trusted-workload deployment. It neither upgrades
an existing installation nor touches the five pre-existing CI services.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tarfile
import time
import urllib.request
import uuid

PROTECTED = ['ag1', 'ctrl', 'jenkins-bench', 'jenkins-lab', 'mcloving-faceoff2']


def command(argv, data=None):
    p = subprocess.run(argv, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=180)
    if p.returncode:
        # Never reflect argv/env/SQL or arbitrary subprocess stderr containing secrets.
        raise RuntimeError('deployment command failed: ' + Path(argv[0]).name)
    return p.stdout.decode().strip()


def protected():
    rows = json.loads(command(['podman', 'inspect'] + PROTECTED))
    return sorted([{'name': r['Name'].lstrip('/'), 'id': r['Id'], 'image': r['Image'],
                    'started_at': r['State']['StartedAt'], 'pid': r['State']['Pid'],
                    'status': r['State']['Status']} for r in rows], key=lambda r: r['name'])


def write(path, data):
    path.write_text(data)
    path.chmod(0o600)


def main():
    a = argparse.ArgumentParser(description=__doc__)
    a.add_argument('--release-archive', type=Path, required=True)
    a.add_argument('--release-sha256', required=True)
    a.add_argument('--packages-archive', type=Path, required=True)
    a.add_argument('--packages-sha256', required=True)
    a.add_argument('--image', required=True)
    args = a.parse_args()
    assert command(['hostname']) == 'luigi'
    assert len(args.image) == 64 and all(c in '0123456789abcdef' for c in args.image)
    root = Path.home() / 'services/fogell'
    unit = Path.home() / '.config/systemd/user/fogell.service'
    dbunit = unit.with_name('fogell-postgres.service')
    assert not root.exists() and not unit.exists() and not dbunit.exists(), 'installation already exists'
    with socket.socket() as listener_probe:
        listener_probe.bind(('127.0.0.1',46206))
    for kind, name in [('container','fogell-controller-20260923'),
                       ('container','fogell-postgres-20260923'), ('volume','fogell-postgres-20260923')]:
        probe = subprocess.run(['podman',kind,'exists',name], capture_output=True, timeout=10)
        assert probe.returncode == 1, 'deployment resource exists or absence cannot be established'
    before = protected()
    assert all(r['status'] == 'running' for r in before)
    for p, sha in [(args.release_archive, args.release_sha256), (args.packages_archive, args.packages_sha256)]:
        assert hashlib.sha256(p.read_bytes()).hexdigest() == sha, 'archive digest differs'
    root.mkdir(parents=True, mode=0o700)
    for directory in ['private', 'state', 'release', 'packages', 'campaigns', 'backups']:
        (root / directory).mkdir(mode=0o700)
    write(root/'protected-before.json', json.dumps(before, indent=2)+'\n')
    for archive, destination in [(args.release_archive, root/'release'), (args.packages_archive, root/'packages')]:
        with tarfile.open(archive) as t:
            t.extractall(destination, filter='data')
    manifest = json.loads((root/'release/release-manifest.json').read_text())
    for relative, sha in manifest['files'].items():
        assert hashlib.sha256((root/'release'/relative).read_bytes()).hexdigest() == sha
    password = secrets.token_hex(32)
    runtime_password = secrets.token_hex(32)
    write(root/'private/token', secrets.token_urlsafe(48)+'\n')
    write(root/'private/postgres.env', f'POSTGRES_USER=fogell\nPOSTGRES_PASSWORD={password}\nPOSTGRES_DB=fogell\n')
    volume = command(['podman', 'volume', 'create', '--label', 'fogell.owner=luigi-pilot-20260923', 'fogell-postgres-20260923'])
    pgimage = command(['podman', 'image', 'inspect', 'docker.io/library/postgres:16', '--format', '{{.Id}}'])
    pg = command(['podman', 'create', '--name', 'fogell-postgres-20260923', '--label', 'fogell.owner=luigi-pilot-20260923',
                  '--cpus', '2', '--memory', '2g', '--memory-swap', '2g', '--pids-limit', '256',
                  '--log-opt', 'max-size=16mb', '--env-file', str(root/'private/postgres.env'),
                  '-p', '127.0.0.1::5432', '-v', volume+':/var/lib/postgresql/data', pgimage])
    command(['podman', 'start', pg])
    for _ in range(120):
        p = subprocess.run(['podman', 'exec', pg, 'pg_isready', '-h', '127.0.0.1', '-U', 'fogell', '-d', 'fogell'], capture_output=True, timeout=10)
        if p.returncode == 0:
            break
        time.sleep(.5)
    else:
        raise RuntimeError('private database did not become ready')
    port = int(command(['podman', 'port', pg, '5432/tcp']).rsplit(':', 1)[1])
    maintenance = f'Host=127.0.0.1;Port={port};Username=fogell;Password={password};Database=fogell'
    runtime = f'Host=127.0.0.1;Port={port};Username=fogell_runtime;Password={runtime_password};Database=fogell'
    env = {'FOGELL_DATABASE_URL': runtime, 'FOGELL_MAINTENANCE_DATABASE_URL': maintenance,
           'FOGELL_API_TOKEN_FILE': '/run/fogell/token', 'FOGELL_LISTEN_URL': 'http://127.0.0.1:46206',
           'FOGELL_STATE_ROOT': '/data', 'FOGELL_RUN_HOST_PATH': '/app/tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host',
           'FOGELL_LOCAL_TRUST_POOL': 'trusted-linux', 'FOGELL_MAX_PIPELINE_BYTES': '16777216',
           'FOGELL_MAX_LOG_CHUNKS': '100', 'FOGELL_WORKER_POLL_MS': '100', 'FOGELL_WORKER_LEASE_SECONDS': '30',
           'FOGELL_ARTIFACT_MAX_FILE_BYTES': '1048576', 'FOGELL_ARTIFACT_MAX_TOTAL_BYTES': '4194304',
           'FOGELL_ARTIFACT_MAX_FILES': '100', 'FOGELL_ARTIFACT_MAX_SCAN_ENTRIES': '10000',
           'HOME': '/tmp/controller-home', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1'}
    write(root/'private/controller.env', ''.join(k+'='+v+'\n' for k,v in env.items()))
    maintenance_run = ['podman', 'run', '--rm', '--network', 'host', '--read-only', '--tmpfs', '/tmp:rw,size=256m',
                       '--env-file', str(root/'private/controller.env'), '-v', str(root/'release')+':/app:ro', args.image]
    command(maintenance_run + ['dotnet', '/app/tools/Fogell.Retention/bin/Release/net10.0/Fogell.Retention.dll', 'migrate'])
    organization, project = str(uuid.uuid4()), str(uuid.uuid4())
    sql = f"""CREATE ROLE fogell_runtime LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{runtime_password}';
GRANT USAGE ON SCHEMA public TO fogell_runtime;
GRANT SELECT, UPDATE(singleton) ON controller_metadata TO fogell_runtime;
GRANT SELECT ON organization_work_roots, build_retention TO fogell_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON organizations,projects,builds,nodes,attempts,events,outbox,log_chunks,effect_checkpoints,retry_decisions,build_definitions,source_verifications TO fogell_runtime;
GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO fogell_runtime;
INSERT INTO organizations(id,slug) VALUES('{organization}','luigi-pilot');
INSERT INTO projects(id,organization_id,slug) VALUES('{project}','{organization}','self-host');
"""
    command(['podman','exec','-i',pg,'psql','-X','-U','fogell','-d','fogell','-v','ON_ERROR_STOP=1'], sql.encode())
    ctl = command(['podman','create','--name','fogell-controller-20260923','--label','fogell.owner=luigi-pilot-20260923',
                   '--network','host','--read-only','--cpus','8','--memory','8g','--memory-swap','8g','--pids-limit','512',
                   '--log-opt','max-size=16mb',
                   '--tmpfs','/tmp:rw,size=256m,mode=1777','--env-file',str(root/'private/controller.env'),
                   '-v',str(root/'release')+':/app:ro','-v',str(root/'state')+':/data:rw',
                   '-v',str(root/'private/token')+':/run/fogell/token:ro',
                   '-v',str(root/'packages')+':/tmp/fogell-pilot-luigi/packages:ro',
                   '-v',str(root/'campaigns')+':/campaign:rw',args.image,
                   '/app/src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host'])
    unit.parent.mkdir(parents=True, exist_ok=True)
    def service(identity, description, requires=''):
        return f'''[Unit]
Description={description}
{requires}
[Service]
Type=simple
ExecStart=/usr/bin/podman start --attach {identity}
ExecStop=/usr/bin/podman stop --time 20 {identity}
Restart=on-failure
RestartSec=3
TimeoutStopSec=40

[Install]
WantedBy=default.target
'''
    # Detach our bootstrap database, then let systemd own its foreground attach.
    command(['podman','stop','--time','20',pg])
    write(dbunit, service(pg, 'Fogell private PostgreSQL (Luigi pilot)'))
    write(unit, service(ctl, 'Fogell controller (trusted Luigi pilot)',
                        'Wants=fogell-postgres.service\nAfter=fogell-postgres.service'))
    command(['systemctl','--user','daemon-reload'])
    command(['systemctl','--user','enable','--now','fogell-postgres.service'])
    time.sleep(2)
    command(['systemctl','--user','enable','--now','fogell.service'])
    for _ in range(120):
        try:
            with urllib.request.urlopen('http://127.0.0.1:46206/health/ready', timeout=1) as response:
                if response.status == 200:
                    break
        except Exception:
            time.sleep(.5)
    else:
        raise RuntimeError('controller did not become ready')
    running = json.loads(command(['podman','inspect',ctl]))[0]['State']
    assert running['Running'] and running['Pid'] > 0
    listeners = command(['ss','-H','-ltnp','sport = :46206'])
    assert '127.0.0.1:46206' in listeners and f"pid={running['Pid']}," in listeners, 'readiness listener is not the owned controller'
    config = {'schema_version':1,'url':'http://127.0.0.1:46206','token_file':str(root/'private/token'),
              'organization':organization,'project':project,'controller_container_id':ctl,'postgres_container_id':pg,
              'service_unit':'fogell.service','postgres_service_unit':'fogell-postgres.service',
              'deployment_root':str(root),'state_root':str(root/'state'),'database':'fogell','postgres_port':port,
              'image':args.image,'postgres_image':pgimage,'source_commit':manifest['commit'],
              'release_archive_sha256':args.release_sha256,'packages_archive_sha256':args.packages_sha256,
              'protected_container_ids':[r['id'] for r in before]}
    write(root/'deployment.json', json.dumps(config,indent=2)+'\n')
    assert protected() == before, 'protected services changed'
    print(json.dumps(config))


if __name__ == '__main__':
    main()
