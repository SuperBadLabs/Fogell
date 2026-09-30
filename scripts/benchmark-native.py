#!/usr/bin/env python3
"""Measure native controller admission, feedback, and terminal latency.

Requires a caller-identified disposable PostgreSQL 16 container. The harness
creates and drops only its uniquely named database and runtime role.
"""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUNNER = ROOT / 'tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host'
CLIENT = ROOT / 'tools/Fogell.Client/bin/Release/net10.0/Fogell.Client'
CONTROLLER = ROOT / 'src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host'
RETENTION = ROOT / 'tools/Fogell.Retention/bin/Release/net10.0/Fogell.Retention'


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def run(args, **kwargs):
    return subprocess.run([str(arg) for arg in args], check=True, timeout=60,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs)


def pipeline(command):
    return json.dumps({'version': 1, 'stages': [{'name': 'bench', 'steps': [{'run': command}]}]})


def stop(process):
    if process and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def percentile(values, p):
    ordered = sorted(values)
    if not ordered:
        return None
    index = (len(ordered) - 1) * p / 100
    low = int(index)
    high = min(low + 1, len(ordered) - 1)
    return round(ordered[low] + (ordered[high] - ordered[low]) * (index - low), 3)


def state_bytes(root):
    return sum(p.stat().st_size for p in root.rglob('*') if p.is_file())


def benchmark(root, container, port, runtime, repeats, output):
    require(runtime in ('podman', 'docker') and 0 < port < 65536, 'invalid database transport')
    mapping = run([runtime, 'port', container, '5432/tcp']).stdout.decode().strip()
    require(mapping.endswith(f'127.0.0.1:{port}') or mapping == f'127.0.0.1:{port}',
            'container/port mismatch')
    identity = uuid.uuid4().hex
    database, role = 'fogell_bench_' + identity, 'fogell_bench_runtime_' + identity
    made_db = made_role = False
    process = None
    peak_rss = [0]
    sampling = threading.Event()

    def sql(db, statement):
        return run([runtime, 'exec', container, 'psql', '-X', '-U', 'fogell', '-d', db,
                    '-v', 'ON_ERROR_STOP=1', '-c', statement])

    try:
        version = run([runtime, 'exec', container, 'psql', '-X', '-U', 'fogell', '-d', 'postgres',
                       '-At', '-c', 'SHOW server_version_num']).stdout.decode().strip()
        require(version.startswith('16'), f'expected PostgreSQL 16, found server_version_num={version}')
        sql('postgres', f'CREATE DATABASE {database}')
        made_db = True
        sql('postgres', f'CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS')
        made_role = True
        admin = f'Host=127.0.0.1;Port={port};Username=fogell;Database={database}'
        base_env = dict(os.environ, FOGELL_MAINTENANCE_DATABASE_URL=admin)
        run([RETENTION, 'migrate'], env=base_env)
        sql(database, f'''GRANT USAGE ON SCHEMA public TO {role};
          GRANT SELECT, UPDATE(singleton) ON controller_metadata TO {role};
          GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes, attempts,
            events, outbox, log_chunks, effect_checkpoints, retry_decisions, build_definitions, source_verifications TO {role};
          GRANT SELECT ON organization_work_roots, build_retention TO {role};
          GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {role}''')
        org, project = str(uuid.uuid4()), str(uuid.uuid4())
        sql(database, f"INSERT INTO organizations(id,slug) VALUES('{org}','benchmark'); INSERT INTO projects(id,organization_id,slug) VALUES('{project}','{org}','benchmark')")
        token = secrets.token_hex(32)
        token_file = root / 'token'
        token_file.write_text(token)
        token_file.chmod(0o600)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            api_port = listener.getsockname()[1]
        url = f'http://127.0.0.1:{api_port}'
        env = dict(base_env, FOGELL_DATABASE_URL=admin + f';Options=-c role={role};No Reset On Close=true',
                   FOGELL_API_TOKEN_FILE=str(token_file), FOGELL_LISTEN_URL=url,
                   FOGELL_STATE_ROOT=str(root / 'state'), FOGELL_RUN_HOST_PATH=str(RUNNER),
                   FOGELL_LOCAL_TRUST_POOL='trusted-linux', FOGELL_MAX_PIPELINE_BYTES='262144',
                   FOGELL_MAX_LOG_CHUNKS='100', FOGELL_WORKER_POLL_MS='50', FOGELL_WORKER_LEASE_SECONDS='10')

        def request(path, payload=None, key=None):
            headers = {'Authorization': 'Bearer ' + token}
            if key:
                headers['Idempotency-Key'] = key
            if payload is not None:
                headers['Content-Type'] = 'application/vnd.fogell.pipeline.v1+json'
            req = urllib.request.Request(url + path, data=payload, headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=5) as response:
                    return response.status, response.read(2**20)
            except urllib.error.HTTPError as error:
                return error.code, error.read(2**20)

        with (root / 'controller.log').open('wb') as log:
            process = subprocess.Popen([str(CONTROLLER)], env=env, stdout=log, stderr=log)

            def sample_rss():
                while not sampling.wait(.02):
                    try:
                        statm = Path(f'/proc/{process.pid}/status').read_text()
                        line = next(x for x in statm.splitlines() if x.startswith('VmRSS:'))
                        peak_rss[0] = max(peak_rss[0], int(line.split()[1]) * 1024)
                    except (OSError, StopIteration, ValueError):
                        pass

            sampler = threading.Thread(target=sample_rss, daemon=True)
            sampler.start()
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                require(process.poll() is None, 'controller exited before readiness')
                try:
                    if request('/health/ready')[0] == 200:
                        break
                except (OSError, urllib.error.URLError):
                    pass
                time.sleep(.05)
            else:
                raise RuntimeError('controller readiness timed out')

            source = root / 'source'
            source.mkdir()
            (source / 'input.txt').write_text('fogell benchmark source v1\n')
            inventory = root / 'files.txt'
            inventory.write_text('input.txt\n')
            pipeline_file, snapshot = root / 'pipeline.json', root / 'snapshot.json'
            pipeline_file.write_text(pipeline("cat input.txt"))
            common = ['--url', url, '--organization', org, '--project', project, '--token-file', token_file]
            builds = f'/api/v1/organizations/{org}/projects/{project}/builds'
            results = []
            failures = []
            for index in range(repeats):
                if snapshot.exists():
                    snapshot.unlink()
                run([CLIENT, 'snapshot', '--pipeline', pipeline_file, '--source-root', source,
                     '--files-from', inventory, '--output', snapshot, '--parent-loop', uuid.uuid4()])
                key = f'bench-{identity}-{index}'
                submitted_at = time.monotonic()
                admission = json.loads(run([CLIENT, 'submit', *common, '--snapshot', snapshot,
                                            '--idempotency-key', key]).stdout)
                accepted_at = time.monotonic()
                build, attempt = admission['build_id'], admission['attempt_id']
                first_feedback = terminal_at = None
                pages_seen = 0
                feedback_sequences = set()
                status = None
                expires = time.monotonic() + 90
                try:
                    while time.monotonic() < expires:
                        code, raw = request(f'{builds}/{build}/feedback')
                        require(code == 200, f'feedback HTTP {code}')
                        page = json.loads(raw)
                        chunks = page.get('chunks', [])
                        pages_seen += 1
                        feedback_sequences.update(chunk.get('sequence') for chunk in chunks
                                                  if chunk.get('sequence') is not None)
                        if chunks and first_feedback is None:
                            first_feedback = time.monotonic()
                        if page.get('is_terminal') and not page.get('has_more'):
                            terminal_at = time.monotonic()
                            status = page['status']
                            break
                        time.sleep(.05)
                    require(terminal_at is not None, 'terminal feedback timed out')
                    require(status == 'success', f'build terminal status was {status}')
                    require(first_feedback is not None, 'no feedback records returned')
                    results.append({'index': index, 'build_id': build, 'attempt_id': attempt,
                                    'status': status, 'accepted_to_first_feedback_ms': round((first_feedback-accepted_at)*1000, 3),
                                    'accepted_to_terminal_ms': round((terminal_at-accepted_at)*1000, 3),
                                    'submit_round_trip_ms': round((accepted_at-submitted_at)*1000, 3),
                                    'feedback_pages_polled': pages_seen,
                                    'feedback_records': len(feedback_sequences)})
                except Exception as error:
                    failures.append({'index': index, 'build_id': build, 'error': str(error)})
            sampling.set()
            sampler.join(timeout=1)
            output.parent.mkdir(parents=True, exist_ok=True)
            successful = [r for r in results]
            first = [r['accepted_to_first_feedback_ms'] for r in successful]
            terminal = [r['accepted_to_terminal_ms'] for r in successful]
            submit = [r['submit_round_trip_ms'] for r in successful]
            result = {'format_version': 1, 'benchmark': 'fogell-native-controller',
                      'revision': run(['git', 'rev-parse', 'HEAD'], cwd=ROOT).stdout.decode().strip(),
                      'container': container, 'postgres_major': 16, 'repeats_requested': repeats,
                      'repeats_succeeded': len(results), 'failures': failures, 'runs': results,
                      'summary': {'submit_round_trip_ms': {'p50': percentile(submit, 50), 'p95': percentile(submit, 95)},
                                  'accepted_to_first_feedback_ms': {'p50': percentile(first, 50), 'p95': percentile(first, 95)},
                                  'accepted_to_terminal_ms': {'p50': percentile(terminal, 50), 'p95': percentile(terminal, 95)},
                                  'feedback_records_total': sum(x['feedback_records'] for x in results),
                                  'state_bytes': state_bytes(root / 'state'), 'controller_peak_rss_bytes': peak_rss[0] or None}}
            output.write_text(json.dumps(result, indent=2, sort_keys=True) + '\n')
            print(json.dumps(result['summary'], indent=2, sort_keys=True))
            require(not failures and len(results) == repeats, f'{len(failures)} benchmark submissions failed; raw output: {output}')
    finally:
        sampling.set()
        stop(process)
        if made_db:
            sql('postgres', f'DROP DATABASE {database} WITH (FORCE)')
        if made_role:
            sql('postgres', f'DROP ROLE {role}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--container', required=True, help='explicit disposable PostgreSQL 16 container name')
    parser.add_argument('--port', required=True, type=int, help='published loopback PostgreSQL port')
    parser.add_argument('--runtime', choices=['podman', 'docker'], default='podman')
    parser.add_argument('--repeats', type=int, default=10)
    parser.add_argument('--output', type=Path, default=Path('native-benchmark.json'))
    args = parser.parse_args()
    require(1 <= args.repeats <= 200, '--repeats must be between 1 and 200')
    require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', args.container), 'invalid container name')
    with tempfile.TemporaryDirectory(prefix='fogell-native-benchmark-') as temp:
        benchmark(Path(temp), args.container, args.port, args.runtime, args.repeats, args.output.resolve())


if __name__ == '__main__':
    main()
