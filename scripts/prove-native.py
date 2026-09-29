#!/usr/bin/env python3
"""Bounded native runner/recovery checks; optional isolated controller proof."""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUNNER = ROOT / 'tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host'
CLIENT = ROOT / 'tools/Fogell.Client/bin/Release/net10.0/Fogell.Client'
CONTROLLER = ROOT / 'src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host'
RETENTION = ROOT / 'tools/Fogell.Retention/bin/Release/net10.0/Fogell.Retention'


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def command(args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, timeout=60,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs)


def pipeline(steps):
    return json.dumps({'version': 1, 'stages': [{'name': 'verify', 'steps': steps}]})


def until(probe, seconds=20):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = probe()
        if value:
            return value
        time.sleep(.05)
    raise RuntimeError('bounded observation timed out')


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def runner_proof(root):
    source, workspace, journal = root/'pipeline.json', root/'workspaces', root/'run.journal'
    source.write_text(pipeline([{'echo': 'native runner output'}, {'run': 'printf done > artifact.txt'}, {'archive': 'artifact.txt'}]))
    args = [RUNNER, source, workspace, 'build', journal]
    require(b'native runner output' in command(args).stdout, 'native runner output missing')
    require((workspace/'_artifacts/build/artifact.txt').read_text() == 'done', 'artifact missing')
    source.unlink()
    require(b'already-terminal' in command(args).stdout, 'terminal journal replay changed')
    # Invalid admission must leave both journal and workspace absent.
    bad = root/'bad.json'
    bad.write_text('{"version":2,"stages":[]}')
    outcome = subprocess.run([str(RUNNER), str(bad), str(root/'bad-ws'), 'build', str(root/'bad.journal')],
                             capture_output=True, timeout=10)
    require(outcome.returncode != 0 and not (root/'bad.journal').exists()
            and not (root/'bad-ws').exists(), 'invalid admission mutated execution state')
    bad.write_text(json.dumps({'version':1,'env':{'NAME\n':'x'},
                               'stages':[{'name':'verify','steps':[{'echo':'x'}]}]}))
    outcome = subprocess.run([str(RUNNER), str(bad), str(root/'bad-ws'), 'build', str(root/'bad.journal')],
                             capture_output=True, timeout=10)
    require(outcome.returncode == 2 and b'invalid environment name' in outcome.stderr
            and not (root/'bad.journal').exists() and not (root/'bad-ws').exists(),
            'newline environment name reached execution state')
    # A real kill after the effect starts must never replay that effect.
    source.write_text(pipeline([{'run': 'printf started >> effect; sleep 30; printf forbidden > after'}]))
    args[-1] = root/'crash.journal'
    with (root/'crash.log').open('wb') as log:
        process = subprocess.Popen([str(a) for a in args], stdout=log, stderr=log, start_new_session=True)
        try:
            until(lambda: (workspace/'build/effect').exists())
            process.kill()
            process.wait(timeout=5)
        finally:
            stop(process)
    resumed = subprocess.run([str(a) for a in args], capture_output=True, timeout=10)
    require(resumed.returncode == 3 and b'needs-reconciliation' in resumed.stderr, 'interrupted execution replayed')
    require((workspace/'build/effect').read_text() == 'started', 'effect repeated')
    require(not (workspace/'build/after').exists(), 'post-kill effect ran')
    source.write_text(pipeline([{'echo': 'different definition'}]))
    changed = subprocess.run([str(a) for a in args], capture_output=True, timeout=10)
    require(changed.returncode == 4 and b'definition-changed' in changed.stderr, 'hybrid resume admitted')
    print('PASS native runner: artifacts, terminal replay, admission, crash reconciliation, changed-definition refusal')


def controller_proof(root, container, port, runtime):
    require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', container), 'invalid container name')
    require(runtime in ('podman', 'docker') and 0 < port < 65536, 'invalid database transport')
    # The caller must explicitly identify a disposable database container.
    mapping = command([runtime, 'port', container, '5432/tcp']).stdout.decode().strip()
    require(mapping.endswith(f'127.0.0.1:{port}') or mapping == f'127.0.0.1:{port}', 'container/port mismatch')
    identity = uuid.uuid4().hex
    database, role = 'fogell_native_' + identity, 'fogell_native_runtime_' + identity
    created_database = created_role = False
    process = None
    def sql(db, statement):
        return command([runtime, 'exec', container, 'psql', '-X', '-U', 'fogell', '-d', db,
                        '-v', 'ON_ERROR_STOP=1', '-c', statement])
    try:
        sql('postgres', f'CREATE DATABASE {database}')
        created_database = True
        sql('postgres', f'CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS')
        created_role = True
        admin = f'Host=127.0.0.1;Port={port};Username=fogell;Database={database}'
        env = dict(os.environ, FOGELL_MAINTENANCE_DATABASE_URL=admin)
        command([RETENTION, 'migrate'], env=env)
        sql(database, f'''GRANT USAGE ON SCHEMA public TO {role};
          GRANT SELECT, UPDATE(singleton) ON controller_metadata TO {role};
          GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes, attempts,
            events, outbox, log_chunks, effect_checkpoints, retry_decisions, build_definitions, source_verifications TO {role};
          GRANT SELECT ON organization_work_roots, build_retention TO {role};
          GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {role}''')
        org, project = str(uuid.uuid4()), str(uuid.uuid4())
        sql(database, f"INSERT INTO organizations(id,slug) VALUES('{org}','native'); INSERT INTO projects(id,organization_id,slug) VALUES('{project}','{org}','native')")
        token = secrets.token_hex(32)
        token_file = root/'token'
        token_file.write_text(token)
        token_file.chmod(0o600)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            api_port = listener.getsockname()[1]
        url = f'http://127.0.0.1:{api_port}'
        env.update(FOGELL_DATABASE_URL=admin+f';Options=-c role={role};No Reset On Close=true',
                   FOGELL_API_TOKEN_FILE=str(token_file), FOGELL_LISTEN_URL=url,
                   FOGELL_STATE_ROOT=str(root/'state'), FOGELL_RUN_HOST_PATH=str(RUNNER),
                   FOGELL_LOCAL_TRUST_POOL='trusted-linux', FOGELL_MAX_PIPELINE_BYTES='262144',
                   FOGELL_MAX_LOG_CHUNKS='100', FOGELL_WORKER_POLL_MS='50', FOGELL_WORKER_LEASE_SECONDS='10')
        def request(path, payload=None, auth=True, key=None):
            headers = {'Authorization': 'Bearer '+token} if auth else {}
            if key:
                headers['Idempotency-Key'] = key
            if payload is not None:
                headers['Content-Type'] = 'application/vnd.fogell.pipeline.v1+json'
            req = urllib.request.Request(url+path, data=payload, headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=3) as response:
                    return response.status, response.read(2**20)
            except urllib.error.HTTPError as error:
                return error.code, error.read(2**20)
        with (root/'controller.log').open('wb') as log:
            process = subprocess.Popen([str(CONTROLLER)], env=env, stdout=log, stderr=log, start_new_session=True)
            def ready():
                require(process.poll() is None, 'controller exited before readiness')
                try:
                    return request('/health/ready')[0] == 200
                except (OSError, urllib.error.URLError):
                    return False
            until(ready)
            builds = f'/api/v1/organizations/{org}/projects/{project}/builds'
            require(request(builds, b'{}', auth=False)[0] == 401, 'unauthenticated admission accepted')
            require(request(builds, b'not JSON', key='bad')[0] == 422, 'malformed definition accepted')
            require(request(builds, b'{"version":2,"stages":[]}', key='bad')[0] == 422, 'unsupported version accepted')
            invalid_root = json.loads(pipeline([{'echo':'x'}]))
            invalid_root['env'] = {'NAME\n':'x'}
            invalid_step = json.loads(pipeline([{'echo':'x','env':{'NAME\r\n':'x'}}]))
            for name, definition in [('root',invalid_root),('step',invalid_step)]:
                require(request(builds, json.dumps(definition).encode(), key='bad-env-'+name)[0] == 422,
                        f'{name} newline environment name was admitted')
            source = root/'source'
            source.mkdir()
            (source/'input.txt').write_text('native source identity')
            inventory = root/'files.txt'
            inventory.write_text('input.txt\n')
            pipeline_file, snapshot = root/'pipeline.json', root/'snapshot.json'
            common = ['--url', url, '--organization', org, '--project', project, '--token-file', token_file]
            for index, exit_code in enumerate([7, 0]):
                pipeline_file.write_text(pipeline([{'run': f'cat input.txt; exit {exit_code}'},
                                                  {'archive':'input.txt', 'always':True}]))
                if snapshot.exists():
                    snapshot.unlink()
                command([CLIENT, 'snapshot', '--pipeline', pipeline_file, '--source-root', source,
                         '--files-from', inventory, '--output', snapshot, '--parent-loop', uuid.uuid4()])
                admission = json.loads(command([CLIENT, 'submit', *common, '--snapshot', snapshot,
                                               '--idempotency-key', 'loop-'+str(index)]).stdout)
                build, attempt = admission['build_id'], admission['attempt_id']
                watched = subprocess.run([str(a) for a in [CLIENT,'watch',*common,'--build',build,'--watch-timeout-seconds','30','--poll-interval-ms','50']],
                                         capture_output=True, timeout=40)
                pages = [json.loads(line) for line in watched.stdout.splitlines() if line]
                require(pages and pages[-1]['is_terminal'] and not pages[-1]['has_more'], 'feedback did not settle')
                require(pages[-1]['status'] == ('failure' if exit_code else 'success'), 'unexpected native build result')
                require(pages[-1]['source_identity']['verification_state'] == 'verified', 'source not verified')
                if exit_code:
                    require(watched.returncode != 0 and any(c.get('diagnostic',{}).get('exit_code') == 7
                            for p in pages for c in p['chunks'] if c.get('diagnostic')), 'typed failure missing')
                else:
                    require(watched.returncode == 0, 'corrected watch did not succeed')
                status, data = request(f'{builds}/{build}/attempts/{attempt}/artifacts/input.txt')
                require(status == 200 and data == b'native source identity', 'artifact identity differs')
                downloaded = command([CLIENT,'source',*common,'--build',build]).stdout
                require(json.loads(downloaded) == json.loads(snapshot.read_bytes()), 'retained source differs')
            # A failed JUnit case is a completed unstable step. Its diagnostic
            # remains available while later work and always publication run.
            junit_steps = [
                {'run': 'printf \'<testsuite><testcase name="bad" classname="tests"><failure message="broken"/></testcase></testsuite>\' > report.xml'},
                {'test_report': 'report.xml'},
                {'echo': 'after-report'},
                {'archive': 'report.xml', 'always': True},
            ]
            status, raw = request(builds, pipeline(junit_steps).encode(), key='junit-failure')
            require(status == 201, 'failed-JUnit pipeline was not admitted')
            junit_build = json.loads(raw)
            def junit_feedback():
                code, raw = request(f"{builds}/{junit_build['build_id']}/feedback")
                require(code == 200, 'failed-JUnit feedback unavailable')
                value = json.loads(raw)
                return value if value['is_terminal'] and not value['has_more'] else None
            feedback = until(junit_feedback)
            require(feedback['status'] == 'unstable', 'failed JUnit did not remain unstable')
            require(any('after-report' in chunk['body'] for chunk in feedback['chunks']),
                    'work after failed JUnit was skipped')
            require(any((chunk.get('diagnostic') or {}).get('category') == 'test'
                        and (chunk.get('diagnostic') or {}).get('test_name') == 'bad'
                        for chunk in feedback['chunks']), 'failed-JUnit diagnostic missing')
            status, data = request(f"{builds}/{junit_build['build_id']}/attempts/{junit_build['attempt_id']}/artifacts/report.xml")
            require(status == 200 and b'<failure message="broken"/>' in data,
                    'failed-JUnit artifact was not published')
            # Keep the original default shell tracing on: it is the burst that
            # exposed the bounded callback queue in the Luigi campaign.
            burst_command = "i=0; while [ $i -lt 350 ]; do printf 'page-marker-%s\\n' \"$i\"; i=$((i+1)); done"
            status, raw = request(builds, pipeline([
                {'run': burst_command},
                {'run': 'printf burst-done > burst.txt'},
                {'archive': 'burst.txt'},
            ]).encode(), key='traced-burst')
            require(status == 201, 'traced burst was not admitted')
            burst = json.loads(raw)
            def burst_feedback():
                code, raw = request(f"{builds}/{burst['build_id']}/feedback")
                require(code == 200, 'traced burst feedback unavailable')
                value = json.loads(raw)
                return value if value['is_terminal'] else None
            require(until(burst_feedback, 60)['status'] == 'success', 'traced burst did not succeed')
            cursor, sequences, bodies, pages = 0, [], [], 0
            while True:
                code, raw = request(f"{builds}/{burst['build_id']}/feedback?from={cursor}")
                require(code == 200, 'traced burst feedback page unavailable')
                page = json.loads(raw)
                require(len(page['chunks']) <= 100, 'traced burst page exceeded its bound')
                sequences.extend(chunk['sequence'] for chunk in page['chunks'])
                bodies.extend(chunk['body'] for chunk in page['chunks'])
                pages += 1
                if not page['has_more']:
                    break
                require(page['next_sequence'] > cursor, 'traced burst cursor did not advance')
                cursor = page['next_sequence']
            markers = [body for body in bodies if body.startswith('page-marker-')]
            require(pages > 1 and sequences == sorted(set(sequences)),
                    'traced burst feedback duplicated or reordered sequences')
            require(markers == [f'page-marker-{i}' for i in range(350)],
                    'traced burst lost, duplicated, or reordered markers')
            status, data = request(f"{builds}/{burst['build_id']}/attempts/{burst['attempt_id']}/artifacts/burst.txt")
            require(status == 200 and data == b'burst-done', 'traced burst artifact differs')
            print(f'PASS traced burst: {pages} pages, {len(sequences)} ordered records, 350 markers and artifact')
            # Cancellation is explicit and observed through terminal feedback.
            status, raw = request(builds, pipeline([{'run':'sleep 30'}]).encode(), key='cancel')
            require(status == 201, 'cancellation control not admitted')
            build = json.loads(raw)['build_id']
            require(request(f'{builds}/{build}/cancel', b'')[0] == 202, 'cancel not acknowledged')
            def cancelled():
                code, raw = request(f'{builds}/{build}/feedback')
                require(code == 200, 'cancel feedback unavailable')
                value = json.loads(raw)
                return value if value['is_terminal'] else None
            require(until(cancelled)['status'] == 'aborted', 'cancel fabricated success')
        print('PASS native controller: authentication, admission, snapshot failure/fix, failed JUnit, diagnostics, artifacts, source retrieval, cancellation')
    finally:
        if process is not None:
            stop(process)
        if created_database:
            sql('postgres', f'DROP DATABASE {database} WITH (FORCE)')
        if created_role:
            sql('postgres', f'DROP ROLE {role}')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--container')
    parser.add_argument('--port', type=int)
    parser.add_argument('--runtime', default='podman', choices=['podman','docker'])
    args = parser.parse_args()
    require(bool(args.container) == bool(args.port), '--container and --port must be provided together')
    with tempfile.TemporaryDirectory(prefix='fogell-native-proof-') as temp:
        root = Path(temp)
        runner_proof(root)
        if args.container:
            controller_proof(root, args.container, args.port, args.runtime)


if __name__ == '__main__':
    main()
