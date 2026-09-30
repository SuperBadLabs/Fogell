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

ROOT = Path(os.environ['FOGELL_CAMPAIGN_SOURCE'])
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
            source = root/'source'
            source.mkdir()
            (source/'input.txt').write_text('native source identity')
            inventory = root/'files.txt'
            inventory.write_text('input.txt\n')
            pipeline_file, snapshot = root/'pipeline.json', root/'snapshot.json'
            common = ['--url', url, '--organization', org, '--project', project, '--token-file', token_file]
            for index, exit_code in enumerate([7, 0] * 25):
                loop_started = time.monotonic()
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
                print(json.dumps({'feedback_loop':index,'exit_code':exit_code,'seconds':round(time.monotonic()-loop_started,4)}),flush=True)

            # Concurrent HTTP admission, duplicate-key races, and bounded pagination.
            from concurrent.futures import ThreadPoolExecutor
            with ThreadPoolExecutor(max_workers=8) as pool:
                submitted = list(pool.map(lambda i: request(builds, pipeline([{'echo':f'concurrent-{i}'}]).encode(), key=f'parallel-{i}'), range(24)))
            require(all(code == 201 for code, _ in submitted), 'parallel admission lost a request')
            def settled(build):
                code, raw = request(f'{builds}/{build}/feedback')
                require(code == 200, 'feedback unavailable')
                value = json.loads(raw)
                return value if value['is_terminal'] else None
            for code, raw in submitted:
                require(until(lambda: settled(json.loads(raw)['build_id']), 60)['status'] == 'success', 'parallel build failed')
            print('PASS 24 concurrent submissions',flush=True)
            shared = pipeline([{'run':'printf exactly-once >> effect; cat effect'}]).encode()
            with ThreadPoolExecutor(max_workers=8) as pool:
                raced = list(pool.map(lambda _: request(builds, shared, key='shared-race'), range(16)))
            require(sum(code == 201 for code, _ in raced) == 1 and all(code in (200,201) for code,_ in raced), 'idempotency race admitted duplicates')
            identities = {json.loads(raw)['build_id'] for _,raw in raced}
            require(len(identities) == 1, 'idempotency race split build identity')
            shared_build = identities.pop()
            require(until(lambda:settled(shared_build))['status'] == 'success', 'shared build did not succeed')
            require(request(builds, pipeline([{'echo':'changed'}]).encode(), key='shared-race')[0] == 409, 'idempotency mismatch not rejected')
            print('PASS 16 concurrent identical keys: one build; changed definition conflicts',flush=True)
            code, raw = request(builds, pipeline([{'run':"i=0; while [ $i -lt 350 ]; do printf 'page-marker-%s\n' \"$i\"; i=$((i+1)); done"}]).encode(), key='pagination')
            require(code == 201, 'pagination build not admitted')
            paged_build = json.loads(raw)['build_id']
            require(until(lambda:settled(paged_build))['status'] == 'success', 'pagination build failed')
            cursor, sequences, chunks, pages = 0, [], [], 0
            while True:
                code, raw = request(f'{builds}/{paged_build}/feedback?from={cursor}')
                require(code == 200, 'page unavailable')
                value=json.loads(raw)
                require(len(value['chunks']) <= 100, 'feedback page exceeded configured bound')
                sequences.extend(c['sequence'] for c in value['chunks'])
                chunks.extend(c['body'] for c in value['chunks'])
                pages += 1
                if not value['has_more']: break
                require(value['next_sequence'] > cursor, 'cursor did not advance')
                cursor=value['next_sequence']
            require(pages > 1 and len(sequences) == len(set(sequences)) and sequences == sorted(sequences), 'pagination lost sequence identity')
            require(all(f'page-marker-{i}' in chunks for i in range(350)), 'pagination lost command output')
            require(request(f'{builds}/{paged_build}/feedback?from=-1')[0] == 400, 'negative cursor accepted')
            print(f'PASS pagination: {pages} pages, {len(sequences)} distinct records, 350 markers',flush=True)
            # Restart this disposable controller and verify committed feedback is retained.
            stop(process)
            process = subprocess.Popen([str(CONTROLLER)], env=env, stdout=log, stderr=log, start_new_session=True)
            until(ready)
            require(settled(paged_build)['status'] == 'success', 'restart lost committed result')
            require(request(builds, shared, key='shared-race')[0] == 200, 'restart lost idempotency binding')
            print('PASS controller restart preserves terminal result and idempotency',flush=True)
            # Kill while work is running; an uncertain command must be held, never replayed.
            code, raw = request(builds, pipeline([{'run':'printf started >> crash-effect; echo crash-started; sleep 30; printf forbidden > crash-after'}]).encode(), key='crash-control')
            require(code == 201, 'crash control not admitted')
            crash_build=json.loads(raw)['build_id']
            def effect_started():
                code,raw=request(f'{builds}/{crash_build}/feedback')
                return code == 200 and any(c['body'] == 'crash-started' for c in json.loads(raw)['chunks'])
            until(effect_started)
            process.kill()
            process.wait(timeout=5)
            process = subprocess.Popen([str(CONTROLLER)], env=env, stdout=log, stderr=log, start_new_session=True)
            until(ready)
            def held():
                code,raw=request(f'{builds}/{crash_build}/feedback')
                value=json.loads(raw)
                return value if code == 200 and value['status'] == 'reconciliation_required' else None
            observed=until(held, 30)
            require(not observed['is_terminal'], 'uncertainty became terminal truth')
            effects=list((root/'state').rglob('crash-effect'))
            require(len(effects) == 1 and effects[0].read_text() == 'started', 'crashed effect replayed')
            require(not list((root/'state').rglob('crash-after')), 'post-crash command continued')
            print('PASS controller SIGKILL: reconciliation required, effect not replayed, later effect absent',flush=True)

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
        print('PASS native controller: authentication, admission, snapshot failure/fix, diagnostics, artifacts, source retrieval, cancellation')
    finally:
        if process is not None:
            stop(process)
        if (root/'controller.log').exists():
            import shutil
            shutil.copyfile(root/'controller.log', Path(os.environ['FOGELL_CAMPAIGN_LOGS'])/'soak-controller.log')
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
